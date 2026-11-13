using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaInfoKeeper.Store;

namespace MediaInfoKeeper.Web.Handler {
    internal sealed class RestoreMediaInfoRouteHandler {
        private readonly Func<IEnumerable<string>, List<BaseItem>> _expandToTargetItems;

        public RestoreMediaInfoRouteHandler(Func<IEnumerable<string>, List<BaseItem>> expandToTargetItems) {
            _expandToTargetItems = expandToTargetItems;
        }

        public MediaInfoMenuResponse Handle(RestoreMediaInfoRequest request) {
            var response = new MediaInfoMenuResponse();

            if (request?.Ids == null || request.Ids.Length == 0) {
                response.Message = "no items";
                return response;
            }

            if (Plugin.Instance.Options.MainPage?.PlugginEnabled != true) {
                response.Total = request.Ids.Length;
                response.Skipped = request.Ids.Length;
                response.Message = "plugin disabled";
                return response;
            }

            var targetItems = _expandToTargetItems(request.Ids);
            response.Total = targetItems.Count;

            if (targetItems.Count == 0) {
                response.Message = "no supported items";
                Plugin.Instance.Logger.Info(
                    $"ShortcutMenu RestoreMediaInfo result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
                return response;
            }

            foreach (var item in targetItems) {
                response.Processed++;
                try {
                    switch (RestoreSingleItemMediaInfo(item)) {
                        case RestoreState.Restored:
                            response.Succeeded++;
                            break;
                        case RestoreState.Skipped:
                            response.Skipped++;
                            break;
                        default:
                            response.Failed++;
                            break;
                    }
                }
                catch (Exception ex) {
                    response.Failed++;
                    Plugin.Instance.Logger.Error($"快捷菜单恢复媒体信息失败: {item.Path ?? item.Name}");
                    Plugin.Instance.Logger.Error(ex.Message);
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
            }

            response.Message = "completed";
            Plugin.Instance.Logger.Info(
                $"ShortcutMenu RestoreMediaInfo result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
            return response;
        }

        private static RestoreState RestoreSingleItemMediaInfo(BaseItem item) {
            if (!(item is Video) && !(item is Audio)) return RestoreState.Skipped;

            var hasPersistedMediaInfo = Plugin.MediaSourceInfoStore?.HasInFile(item) == true ||
                                        (item is Audio && Plugin.EmbeddedInfoStore?.HasInFile(item) == true);
            if (!hasPersistedMediaInfo) {
                Plugin.Instance.Logger.Info($"快捷菜单恢复媒体信息跳过 无 JSON 媒体信息: {item.Path ?? item.Name}");
                return RestoreState.Skipped;
            }

            var restoreResult =
                Plugin.MediaInfoService.RestorePersistedMediaInfoForExistingSource(item, "快捷菜单");
            if (restoreResult == MediaInfoDocument.MediaInfoRestoreResult.Restored) return RestoreState.Restored;

            if (restoreResult == MediaInfoDocument.MediaInfoRestoreResult.AlreadyExists) return RestoreState.Skipped;

            return RestoreState.Failed;
        }

        private enum RestoreState {
            Restored,
            Skipped,
            Failed
        }
    }
}
