using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch {
    /// <summary>
    ///     拦截 Emby 自身的 ffprobe/ffmpeg 调用，仅允许插件在作用域内显式放行。
    /// </summary>
    public static class FfProcessGuard {
        private static readonly AsyncLocal<ScopeFrame> CurrentScope = new();
        private static Harmony harmony;
        private static MethodInfo runFfProcess;
        private static MethodInfo diagnosticsRunFfProcess;
        private static MethodInfo runExtraction;
        private static PropertyInfo exitCode;
        private static PropertyInfo standardOutput;
        private static PropertyInfo standardError;
        private static object emptyResult;
        private static ILogger logger;
        private static bool isEnabled;

        public static bool IsReady => harmony != null && runFfProcess != null && diagnosticsRunFfProcess != null &&
                                      runExtraction != null && emptyResult != null;

        public static void Initialize(ILogger pluginLogger, bool disableSystemFfprobe) {
            if (harmony != null) return;

            logger = pluginLogger;
            isEnabled = disableSystemFfprobe;

            try {
                var mediaEncoding = Assembly.Load("Emby.Server.MediaEncoding");
                if (mediaEncoding == null) {
                    PatchLog.InitFailed(logger, nameof(FfProcessGuard), "未找到 Emby.Server.MediaEncoding");
                    return;
                }

                var mediaProbeManager = mediaEncoding.GetType("Emby.Server.MediaEncoding.Probing.MediaProbeManager");
                if (mediaProbeManager == null) {
                    PatchLog.InitFailed(logger, nameof(FfProcessGuard), "未找到 MediaProbeManager 类型");
                    return;
                }

                runFfProcess = ResolveRunFfProcess(mediaEncoding, mediaProbeManager);
                diagnosticsRunFfProcess = ResolveDiagnosticsRunFfProcess(mediaEncoding);
                runExtraction = ResolveRunExtraction(mediaEncoding);

                var processRun = Assembly.Load("Emby.ProcessRun");
                var processResult = processRun?.GetType("Emby.ProcessRun.Common.ProcessResult");
                exitCode = FindProperty(processResult, "ExitCode");
                standardOutput = FindProperty(processResult, "StandardOutput");
                standardError = FindProperty(processResult, "StandardError");

                emptyResult = CreateEmptyResult(runFfProcess?.ReturnType);

                if (runFfProcess == null || emptyResult == null) {
                    PatchLog.InitFailed(logger, nameof(FfProcessGuard), "目标方法未找到或返回类型不支持");
                    return;
                }

                PatchLog.Patched(logger, nameof(FfProcessGuard), runFfProcess);

                harmony = new Harmony("mediainfokeeper.ffprobe");

                try {
                    harmony.Patch(runFfProcess,
                        new HarmonyMethod(typeof(FfProcessGuard), nameof(RunFfProcessPrefix)),
                        new HarmonyMethod(typeof(FfProcessGuard), nameof(RunFfProcessPostfix)));
                    harmony.Patch(diagnosticsRunFfProcess,
                        new HarmonyMethod(typeof(FfProcessGuard), nameof(DiagnosticsRunFfProcessPrefix)),
                        new HarmonyMethod(typeof(FfProcessGuard), nameof(RunFfProcessPostfix)));
                    PatchRunExtraction(runExtraction);
                }
                catch (Exception patchEx) {
                    logger.Error("ffprobe/ffmpeg guard patch 失败");
                    logger.Error(patchEx.Message);
                    logger.Error(patchEx.ToString());
                    harmony = null;
                    isEnabled = false;
                }
            }
            catch (Exception e) {
                logger.Error("ffprobe/ffmpeg guard 初始化失败");
                logger.Error(e.Message);
                logger.Error(e.ToString());
                harmony = null;
                isEnabled = false;
                logger.Warn("ffprobe/ffmpeg guard 初始化失败已禁用，ffprobe/ffmpeg 不再拦截");
            }
        }

        public static void Configure(bool disableSystemFfprobe) {
            isEnabled = disableSystemFfprobe;
        }

        /// <summary>
        ///     创建放行作用域，插件内部调用 ffprobe/ffmpeg 时使用。
        /// </summary>
        public static IDisposable Allow() {
            return new GuardScope(BeginAllow());
        }

        public static AllowanceHandle BeginAllow(AllowanceContext context = null) {
            var previous = CurrentScope.Value;
            var handle = new AllowanceHandle(context);
            CurrentScope.Value = new ScopeFrame(handle, previous);
            return handle;
        }

        public static bool HasExplicitAllowance() {
            return FindAllowedScope() != null;
        }

        public static bool HasActiveItemProbe() {
            return FindScopeWithItemContext() != null;
        }

        public static void EndAllow(AllowanceHandle handle = null) {
            var current = CurrentScope.Value;
            if (current == null) return;

            if (handle == null || ReferenceEquals(current.Handle, handle)) CurrentScope.Value = current.Previous;
        }

        private static bool RunFfProcessPrefix(
            object __instance,
            [HarmonyArgument(0)] object runType,
            [HarmonyArgument(1)] string processName,
            [HarmonyArgument(2)] string arguments,
            [HarmonyArgument(3)] ref int timeoutMs,
            [HarmonyArgument(4)] CancellationToken cancellationToken,
            ref object __result,
            out bool __state) {
            __state = false;
            if (!isEnabled) return true;

            var inputHint = ExtractInputHint(arguments);
            var runTypeText = runType?.ToString() ?? string.Empty;
            var isFfprobe = runTypeText.IndexOf("ffprobe", StringComparison.OrdinalIgnoreCase) >= 0;
            var isFfmpeg = runTypeText.IndexOf("ffmpeg", StringComparison.OrdinalIgnoreCase) >= 0;
            __state = isFfprobe;
            if (!isFfprobe && !isFfmpeg) return true;

            var displayTarget = $"{(isFfmpeg ? "ffmpeg" : "ffprobe")} {inputHint}";

            var scope = FindAllowedScope();
            if (scope != null) {
                var context = scope.Handle?.Context;
                logger?.Debug($"允许 {displayTarget}");
                if ((isFfprobe || isFfmpeg) && context != null) context.WasFfProcessCalled = true;

                __state = isFfprobe && context?.ItemInternalId > 0 && context.AllowFfProcess;
                timeoutMs = Math.Max(timeoutMs, 60000);
                return true;
            }

            logger?.Debug($"拦截 {displayTarget}");
            __result = emptyResult;
            return false;
        }

        private static bool DiagnosticsRunFfProcessPrefix(
            object __instance,
            [HarmonyArgument(0)] object runType,
            [HarmonyArgument(1)] string processName,
            [HarmonyArgument(2)] string arguments,
            [HarmonyArgument(3)] ref TimeSpan timeout,
            [HarmonyArgument(4)] bool logInfo,
            [HarmonyArgument(5)] CancellationToken cancellationToken,
            ref object __result,
            out bool __state) {
            __state = false;
            if (!isEnabled) return true;

            var inputHint = ExtractInputHint(arguments);
            var runTypeText = runType?.ToString() ?? string.Empty;
            var isFfprobe = runTypeText.IndexOf("ffprobe", StringComparison.OrdinalIgnoreCase) >= 0;
            var isFfmpeg = runTypeText.IndexOf("ffmpeg", StringComparison.OrdinalIgnoreCase) >= 0;
            __state = isFfprobe;
            if (!isFfprobe && !isFfmpeg) return true;

            var displayTarget = $"{(isFfmpeg ? "ffmpeg" : "ffprobe")} {inputHint}";

            var scope = FindAllowedScope();
            if (scope != null) {
                var context = scope.Handle?.Context;
                logger?.Debug($"允许 {displayTarget}");
                if ((isFfprobe || isFfmpeg) && context != null) context.WasFfProcessCalled = true;

                __state = isFfprobe && context?.ItemInternalId > 0 && context.AllowFfProcess;
                if (timeout < TimeSpan.FromSeconds(60)) timeout = TimeSpan.FromSeconds(60);

                return true;
            }

            logger?.Debug($"拦截 {displayTarget}");
            __result = emptyResult;
            return false;
        }

        private static bool RunExtractionPrefix([HarmonyArgument(1)] string inputPath, ref object __result) {
            if (!isEnabled) return true;

            var inputHint = ExtractInputHint($"-i file:\"{inputPath ?? string.Empty}\"");
            var displayTarget = $"ffmpeg {inputHint}";
            if (HasFfprocessAllowanceInCurrentScope()) {
                logger?.Debug($"允许 {displayTarget}");
                return true;
            }

            logger?.Debug($"拦截 {displayTarget}");
            __result = Task.CompletedTask;
            return false;
        }

        private static void RunFfProcessPostfix(ref object __result, bool __state) {
            if (__result is not Task task) return;

            __result = AwaitProcessTask(task, __state);
        }

        private static string ExtractInputHint(string arguments) {
            if (string.IsNullOrWhiteSpace(arguments)) return string.Empty;

            var match = Regex.Match(arguments, @"-i\s+(file:""[^""]+""|""[^""]+""|\S+)");
            if (!match.Success)
                // 某些 ffprobe 调用会把输入作为最后一个参数直接追加，而不是放在 -i 后面。
                match = Regex.Match(arguments, @"(file:""[^""]+""|""[^""]+://[^""]+""|\S+://\S+)\s*$");

            var value = match.Value
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");

            return value;
        }

        private static object CreateEmptyResult(Type returnType) {
            if (returnType == null) return null;

            if (returnType == typeof(Task)) return Task.CompletedTask;

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)) {
                var resultType = returnType.GetGenericArguments()[0];
                object payload = null;

                try {
                    payload = Activator.CreateInstance(resultType, true);
                    if (payload != null) {
                        try { standardOutput?.SetValue(payload, "{}"); }
                        catch {
                            /* best-effort stub */
                        }

                        try { standardError?.SetValue(payload, "ffprobe/ffmpeg 已被 MediaInfoKeeper 拦截"); }
                        catch {
                            /* best-effort stub */
                        }
                    }
                }
                catch (Exception e) {
                    logger?.Debug(e.Message);
                    logger?.Debug(e.ToString());
                }

                var fromResult = typeof(Task).GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .First(m => m.Name == nameof(Task.FromResult) && m.IsGenericMethod)
                    .MakeGenericMethod(resultType);
                return fromResult.Invoke(null, new[] { payload });
            }

            return null;
        }

        private static MethodInfo ResolveRunFfProcess(Assembly mediaEncoding, Type mediaProbeManager) {
            try {
                var ffRunType = mediaEncoding.GetType("Emby.Server.MediaEncoding.Unified.Ffmpeg.FfRunType");
                if (ffRunType == null) {
                    PatchLog.Candidates(
                        logger,
                        "FfprobeGuard.FfRunType",
                        "未找到类型 Emby.Server.MediaEncoding.Unified.Ffmpeg.FfRunType");
                    return null;
                }

                var assemblyVersion = mediaEncoding.GetName().Version;
                var exactProfile = new MethodSignatureProfile {
                    Name = "runffprocess-exact",
                    MethodName = "RunFfProcess",
                    BindingFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                   BindingFlags.NonPublic,
                    ParameterTypes = new[]
                        { ffRunType, typeof(string), typeof(string), typeof(int), typeof(CancellationToken) }
                };

                return PatchMethodResolver.Resolve(
                    mediaProbeManager,
                    assemblyVersion,
                    exactProfile,
                    logger,
                    "FfprobeGuard.RunFfProcess");
            }
            catch {
                return null;
            }
        }

        private static MethodInfo ResolveDiagnosticsRunFfProcess(Assembly mediaEncoding) {
            try {
                var encodingDiagnostics =
                    mediaEncoding.GetType("Emby.Server.MediaEncoding.Unified.Diagnostics.EncodingDiagnostics");
                if (encodingDiagnostics == null) {
                    PatchLog.InitFailed(logger, nameof(FfProcessGuard), "未找到 EncodingDiagnostics 类型");
                    return null;
                }

                var ffRunType = mediaEncoding.GetType("Emby.Server.MediaEncoding.Unified.Ffmpeg.FfRunType");
                if (ffRunType == null) {
                    PatchLog.InitFailed(logger, nameof(FfProcessGuard), "未找到 FfRunType 类型");
                    return null;
                }

                var assemblyVersion = mediaEncoding.GetName().Version;
                return PatchMethodResolver.Resolve(
                    encodingDiagnostics,
                    assemblyVersion,
                    new MethodSignatureProfile {
                        Name = "diagnostics-runffprocess-exact",
                        MethodName = "RunFfProcess",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[] {
                            ffRunType, typeof(string), typeof(string), typeof(TimeSpan), typeof(bool),
                            typeof(CancellationToken)
                        }
                    },
                    logger,
                    "FfprobeGuard.EncodingDiagnostics.RunFfProcess");
            }
            catch {
                return null;
            }
        }

        private static MethodInfo ResolveRunExtraction(Assembly mediaEncoding) {
            var imageExtractorBaseType =
                mediaEncoding?.GetType("Emby.Server.MediaEncoding.ImageExtraction.ImageExtractorBase");
            if (imageExtractorBaseType == null) {
                PatchLog.InitFailed(logger, nameof(FfProcessGuard), "未找到 ImageExtractorBase 类型");
                return null;
            }

            var mediaBrowserModel = Assembly.Load("MediaBrowser.Model");
            var mediaSourceInfoType = mediaBrowserModel?.GetType("MediaBrowser.Model.Dto.MediaSourceInfo");
            var mediaContainersType = mediaBrowserModel?.GetType("MediaBrowser.Model.MediaInfo.MediaContainers");
            var mediaProtocolType = mediaBrowserModel?.GetType("MediaBrowser.Model.MediaInfo.MediaProtocol");
            var video3DFormatType = mediaBrowserModel?.GetType("MediaBrowser.Model.Entities.Video3DFormat");
            if (mediaSourceInfoType == null || mediaContainersType == null || mediaProtocolType == null ||
                video3DFormatType == null) {
                PatchLog.InitFailed(logger, nameof(FfProcessGuard), "未找到 ImageExtraction 相关依赖类型");
                return null;
            }

            var assemblyVersion = mediaEncoding.GetName().Version;
            return PatchMethodResolver.Resolve(
                imageExtractorBaseType,
                assemblyVersion,
                new MethodSignatureProfile {
                    Name = "run-extraction-exact",
                    MethodName = "RunExtraction",
                    BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    ParameterTypes = new[] {
                        mediaSourceInfoType,
                        typeof(string),
                        typeof(Nullable<>).MakeGenericType(mediaContainersType),
                        typeof(MediaStream),
                        mediaProtocolType,
                        typeof(int?),
                        typeof(Nullable<>).MakeGenericType(video3DFormatType),
                        typeof(TimeSpan?),
                        typeof(TimeSpan?),
                        typeof(string),
                        typeof(string),
                        typeof(int?),
                        typeof(bool),
                        typeof(CancellationToken)
                    },
                    ReturnType = typeof(Task)
                },
                logger,
                "FfprobeGuard.RunExtraction");
        }

        private static void PatchRunExtraction(MethodInfo method) {
            if (method == null) return;

            PatchLog.Patched(logger, nameof(FfProcessGuard), method);
            harmony.Patch(method, new HarmonyMethod(typeof(FfProcessGuard), nameof(RunExtractionPrefix)));
        }

        private static bool HasFfprocessAllowanceInCurrentScope() {
            return FindAllowedScope() != null;
        }

        private static ScopeFrame FindAllowedScope() {
            var scope = CurrentScope.Value;
            while (scope != null) {
                if (scope.Handle?.Context == null || scope.Handle.Context.AllowFfProcess) return scope;

                scope = scope.Previous;
            }

            return null;
        }

        private static object AwaitProcessTask(Task task, bool shouldPersistAfterSuccess) {
            var taskType = task.GetType();
            if (taskType == typeof(Task)) return AwaitTask(task, shouldPersistAfterSuccess);

            var resultType = GetTaskResultType(taskType);
            if (resultType != null) {
                var method = typeof(FfProcessGuard)
                    .GetMethod(nameof(AwaitGenericTask), BindingFlags.Static | BindingFlags.NonPublic)
                    ?.MakeGenericMethod(resultType);
                return method?.Invoke(null, new object[] { task, shouldPersistAfterSuccess }) ?? task;
            }

            return task;
        }

        private static async Task AwaitTask(Task task, bool shouldPersistAfterSuccess) {
            await task.ConfigureAwait(false);
            if (shouldPersistAfterSuccess) _ = PersistCurrentScopeMediaInfoAsync();
        }

        private static async Task<T> AwaitGenericTask<T>(Task<T> task, bool shouldPersistAfterSuccess) {
            var result = await task.ConfigureAwait(false);
            if (result == null) return result;

            var stdout = standardOutput?.GetValue(result) as string;
            var stderr = standardError?.GetValue(result) as string;
            if (stdout != null && stderr != null) {
                var trimmed = new string((stdout ?? string.Empty)
                    .Where(c => !char.IsWhiteSpace(c))
                    .ToArray());
                if (string.Equals(trimmed, "{}", StringComparison.Ordinal)) {
                    var lines = stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0) {
                        var message = lines[lines.Length - 1].Trim();
                        if (!string.IsNullOrEmpty(message)) logger.Error("ffprobe/ffmpeg 错误: " + message);
                    }
                }
            }

            if (shouldPersistAfterSuccess) {
                var exitCodeValue = exitCode?.GetValue(result);
                var nullableExitCode = exitCodeValue as int?;
                if ((exitCodeValue is int exitCodeNumber && exitCodeNumber == 0) ||
                    (nullableExitCode.HasValue && nullableExitCode.GetValueOrDefault(-1) == 0))
                    _ = PersistCurrentScopeMediaInfoAsync();
            }

            return result;
        }

        private static async Task PersistCurrentScopeMediaInfoAsync() {
            if (Plugin.Instance?.Options?.MainPage?.PlugginEnabled != true) return;

            var scope = FindScopeWithItemContext();
            if (scope == null) {
                logger?.Info("ffprobe 落盘未触发: 当前作用域没有条目上下文");
                return;
            }

            var context = scope.Handle.Context;
            for (var attempt = 0; attempt <= 5; attempt++) {
                var item = Plugin.LibraryManager?.GetItemById(context.ItemInternalId);
                var displayName = item?.FileName ?? item?.Path;

                if (item != null && Plugin.MediaInfoService?.HasMediaInfo(item) == true) {
                    logger?.Info($"新提取 {displayName} 媒体信息，覆盖写入Json");
                    _ = Task.Run(async () => {
                        await Task.Delay(3000).ConfigureAwait(false);
                        Plugin.MediaSourceInfoStore?.OverWriteToFile(item);
                        if (item is Audio) Plugin.EmbeddedInfoStore?.OverWriteToFile(item);
                    });
                    return;
                }

                if (attempt < 5) await Task.Delay(1000).ConfigureAwait(false);
            }

            // 提取慢于轮询窗口时这里放弃写入，由入库流程结束后的补写兜底，避免静默漏写。
            logger?.Warn(
                $"ffprobe 落盘未触发: 等待媒体流超时 item={context.ItemPath ?? context.ItemInternalId.ToString()}，改由入库流程结束时补写");
        }

        private static Type GetTaskResultType(Type taskType) {
            var current = taskType;
            while (current != null) {
                if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Task<>)) return current.GetGenericArguments()[0];

                current = current.BaseType;
            }

            return null;
        }

        private static ScopeFrame FindScopeWithItemContext() {
            var scope = CurrentScope.Value;
            while (scope != null) {
                var context = scope.Handle?.Context;
                if (context?.ItemInternalId > 0 && context.AllowFfProcess) return scope;

                scope = scope.Previous;
            }

            return null;
        }

        private static PropertyInfo FindProperty(Type type, string propertyName) {
            var property = type?.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Static |
                                                           BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null) LogPropertyCandidates(type, propertyName);

            return property;
        }

        private static void LogPropertyCandidates(Type type, string propertyName) {
            try {
                var candidates = type?.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                                     BindingFlags.NonPublic)
                    .Where(p => p.Name == propertyName)
                    .Select(p => $"{p.PropertyType?.Name} {p.Name}");

                PatchLog.Candidates(
                    logger,
                    string.Format("{0}.{1}", type?.FullName ?? "<null>", propertyName ?? "<null>"),
                    string.Join("; ", candidates ?? Enumerable.Empty<string>()));
            }
            catch (Exception e) {
                logger?.Debug(e.Message);
                logger?.Debug(e.StackTrace);
            }
        }

        public sealed class AllowanceContext {
            public long ItemInternalId { get; set; }
            public string ItemPath { get; set; }
            public bool IsShortcut { get; set; }
            public bool AllowFfProcess { get; set; }
            public bool WasFfProcessCalled { get; set; }
        }

        public sealed class AllowanceHandle {
            internal AllowanceHandle(AllowanceContext context) {
                Context = context;
            }

            internal AllowanceContext Context { get; }
        }

        private sealed class ScopeFrame {
            public ScopeFrame(AllowanceHandle handle, ScopeFrame previous) {
                Handle = handle;
                Previous = previous;
            }

            public AllowanceHandle Handle { get; }
            public ScopeFrame Previous { get; }
        }

        private sealed class GuardScope : IDisposable {
            private readonly AllowanceHandle handle;

            public GuardScope(AllowanceHandle handle) {
                this.handle = handle;
            }

            public void Dispose() {
                EndAllow(handle);
            }
        }
    }
}
