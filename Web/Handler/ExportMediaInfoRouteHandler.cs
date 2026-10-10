using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;

namespace MediaInfoKeeper.Web.Handler {
    internal sealed class ExportMediaInfoRouteHandler {
        private readonly Func<IEnumerable<string>, List<BaseItem>> _expandToTargetItems;

        public ExportMediaInfoRouteHandler(Func<IEnumerable<string>, List<BaseItem>> expandToTargetItems) {
            _expandToTargetItems = expandToTargetItems;
        }

        public MediaInfoMenuResponse Handle(ExportMediaInfoRequest request) {
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
                    $"ShortcutMenu ExportMediaInfo result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
                return response;
            }

            foreach (var item in targetItems) {
                response.Processed++;
                try {
                    if (!Plugin.MediaInfoService.HasMediaInfo(item)) {
                        response.Skipped++;
                        continue;
                    }

                    Plugin.MediaSourceInfoStore?.OverWriteToFile(item);
                    if (item is Audio)
                        Plugin.EmbeddedInfoStore?.OverWriteToFile(item);
                    else
                        Plugin.ChaptersStore?.OverWriteToFile(item);
                    response.Succeeded++;
                }
                catch (Exception ex) {
                    response.Failed++;
                    Plugin.Instance.Logger.Error($"快捷菜单备份媒体信息失败: {item.Path ?? item.Name}");
                    Plugin.Instance.Logger.Error(ex.Message);
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
            }

            response.Message = "completed";
            Plugin.Instance.Logger.Info(
                $"ShortcutMenu ExportMediaInfo result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
            return response;
        }
    }
}
