using System.Text.Json;
using OpenMono.Config;
using OpenMono.Hooks;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Utils;

namespace OpenMono.Tools;

public sealed class LocalToolExecutor : IToolExecutor
{
    private readonly TurnJournal _journal;
    private readonly IRenderer _renderer;
    private readonly AppConfig _config;
    private readonly SessionState _session;
    private readonly PermissionEngine _permissions;
    private readonly ToolResultCache _cache;
    private readonly ArtifactStore _artifactStore;
    private readonly HookRunner _hookRunner;

    public LocalToolExecutor(
        TurnJournal journal,
        IRenderer renderer,
        AppConfig config,
        SessionState session,
        PermissionEngine permissions,
        ToolResultCache cache,
        ArtifactStore artifactStore,
        HookRunner hookRunner)
    {
        _journal = journal;
        _renderer = renderer;
        _config = config;
        _session = session;
        _permissions = permissions;
        _cache = cache;
        _artifactStore = artifactStore;
        _hookRunner = hookRunner;
    }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ITool? tool, ToolContext ctx, CancellationToken ct)
    {
        if (tool is null)
            return ToolResult.Error($"Unknown tool: {call.Name}");

        _journal.RecordToolCallReceived(call.Id, call.Name, call.Arguments);

        JsonElement input;
        try
        {
            input = JsonDocument.Parse(call.Arguments).RootElement;
        }
        catch (JsonException ex)
        {
            _journal.RecordSchemaRejected(call.Id, $"json_parse: {ex.Message}");
            return ToolResult.Error(
                $"Invalid JSON arguments for {call.Name}: {ex.Message}\nRaw: {call.Arguments[..Math.Min(200, call.Arguments.Length)]}");
        }

        var validationError = SchemaValidator.Validate(tool.Name, tool.InputSchema, input);
        if (validationError is not null)
        {
            _journal.RecordSchemaRejected(call.Id, validationError);
            _renderer.WriteToolDenied(call.Name, validationError);
            Log.Warn($"Tool schema rejected: {call.Name} — {validationError}");
            return ToolResult.Error(validationError);
        }
        _journal.RecordSchemaValidated(call.Id);

        var sanityError = SanityCheck.Check(call.Name, input, _config.WorkingDirectory);
        if (sanityError is not null)
        {
            _journal.RecordSanityRejected(call.Id, sanityError);
            _renderer.WriteToolDenied(call.Name, sanityError);
            Log.Warn($"Tool sanity-rejected: {call.Name} — {sanityError}");
            return ToolResult.Error(sanityError);
        }
        _journal.RecordSanityChecked(call.Id);

        if (_session.Meta.PlanMode && !tool.IsReadOnly)
        {
            var planModeError = $"Plan mode is active — investigate and write a plan, do not edit files." +
                                $"Call ExitPlanMode with your completed plan to resume, then retry {call.Name}.";
            _journal.RecordPermissionDecided(call.Id, false, "plan_mode_active");
            _renderer.WriteToolDenied(call.Name, planModeError);
            return ToolResult.Error(planModeError);
        }

        var capabilities = tool.RequiredCapabilities(input);
        bool allowed;
        string? reason;

        if (capabilities.Count > 0)
        {
            var capDecision = await _permissions.CheckCapabilitiesAsync(tool.Name, capabilities, ct);
            allowed = capDecision.Allowed;
            reason = capDecision.Reason;
        }
        else
        {
            var permLevel = tool.RequiredPermission(input);
            var legacyDecision = await _permissions.CheckAsync(tool.Name, input, permLevel, ct);
            allowed = legacyDecision.Allowed;
            reason = legacyDecision.Reason;
        }

        if (!allowed)
        {
            _journal.RecordPermissionDecided(call.Id, false, reason);
            _renderer.WriteToolDenied(call.Name, reason ?? "Permission denied");
            Log.Info($"Tool denied: {call.Name} — {reason ?? "User denied"}");
            return ToolResult.Error(
                $"Permission denied for {call.Name}: {reason ?? "User denied"}. " +
                $"Do not retry this tool call. Ask the user how to proceed instead.");
        }
        _journal.RecordPermissionDecided(call.Id, true);

        if (tool.IsReadOnly && _cache.TryGet(call.Name, input, out var cachedResult) && cachedResult is not null)
        {
            _journal.RecordToolStarted(call.Id);
            _journal.RecordToolCompleted(call.Id, cachedResult.Class, cachedResult.Artifacts.Select(a => a.Id).ToList());
            _renderer.WriteToolStart(call.Name, call.Arguments);
            _renderer.WriteToolSuccess(call.Name);
            Log.Debug($"Tool cache hit: {call.Name}");
            return cachedResult with { ModelPreview = $"[cached] {cachedResult.ModelPreview}" };
        }

        _renderer.WriteToolStart(call.Name, call.Arguments);

        _session.Meta.TokenTracker?.RecordToolUse(call.Name);

        _journal.RecordToolStarted(call.Id);

        try
        {
            await _hookRunner.RunPreToolUseHooksAsync(call.Name, call.Arguments, ct);

            Log.Debug($"Tool executing: {call.Name}");
            var result = await tool.ExecuteAsync(input, ctx, ct);

            await _hookRunner.RunPostToolUseHooksAsync(call.Name, result.Content, ct);

            if (result.Class == ResultClass.Success && result.ModelPreview.Length > _artifactStore.LargeOutputThreshold)
            {
                result = _artifactStore.PersistAndReplace(result, call.Name);
                Log.Debug($"Tool output persisted as artifact: {call.Name}");
            }

            if (tool.IsReadOnly && result.Class == ResultClass.Success)
            {
                _cache.Put(call.Name, input, result);
            }

            if (!tool.IsReadOnly && call.Name is "FileWrite" or "FileEdit" or "ApplyPatch")
            {
                if (input.TryGetProperty("file_path", out var pathEl) && pathEl.GetString() is { } filePath)
                {
                    var resolvedPath = Path.GetFullPath(filePath, _config.WorkingDirectory);
                    _cache.InvalidatePath(resolvedPath);
                    FileReadTool.InvalidateCache(resolvedPath);
                }
            }

            var artifactIds = result.Artifacts.Select(a => a.Id).ToList();
            _journal.RecordToolCompleted(call.Id, result.Class, artifactIds);

            if (result.IsError)
            {
                _renderer.WriteToolError(call.Name, result.ErrorMessage ?? "Unknown error");
                Log.Warn($"Tool error: {call.Name} — {result.ErrorMessage}");
            }
            else
            {
                _renderer.WriteToolSuccess(call.Name);

                if (call.Name is "FileRead" or "FileWrite" &&
                    input.TryGetProperty("file_path", out var fpProp) &&
                    fpProp.GetString() is { } filePath)
                {
                    var content = call.Name == "FileWrite"
                        ? (input.TryGetProperty("content", out var cp) ? cp.GetString() ?? "" : "")
                        : result.ModelPreview;
                    _renderer.WriteToolContent(call.Name, filePath, content);
                }
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            _journal.RecordToolCrashed(call.Id, "OperationCanceledException", "cancelled");
            Log.Info($"Tool cancelled: {call.Name}");
            return ToolResult.Cancelled($"{call.Name} was cancelled");
        }
        catch (Exception ex)
        {
            _journal.RecordToolCrashed(call.Id, ex.GetType().Name, ex.Message);
            _renderer.WriteToolError(call.Name, ex.Message);
            Log.Error($"Tool exception: {call.Name}", ex);
            return ToolResult.Crash($"Tool execution failed: {ex.Message}", "Try with different parameters or report this as a bug.");
        }
    }
}
