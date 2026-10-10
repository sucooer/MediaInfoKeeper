using System;
using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Editors;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.GenericEdit;

namespace MediaInfoKeeper.Options {
    public class GitHubOptions : EditableOptionsBase {
        public enum UpdateChannelOption {
            Stable,
            Beta
        }

        public override string EditorTitle => "GitHub";

        public override string EditorDescription => string.Empty;

        [DisplayName("GitHub 访问令牌")]
        [Description("设置后使用 Token 获取 Releases，避免未认证请求的限流。")]
        public string GitHubToken { get; set; } = string.Empty;

        [DisplayName("下载前缀")]
        [Description("仅用于插件 Dll 下载，例如 https://ghfast.top 已配置网络代理时通常不需要再设置这里，避免代理链路叠加。")]
        public string DownloadUrlPrefix { get; set; } = string.Empty;

        [Browsable(false)] public List<EditorSelectOption> UpdateChannelList { get; set; } = new();

        [DisplayName("更新频道")]
        [Description("Stable 只拉取最新正式版 Release；Beta 拉取最新 Release，可能是正式版，也可能是预发布版。")]
        [Editor(typeof(EditorSelectSingle), typeof(EditorBase))]
        [SelectItemsSource(nameof(UpdateChannelList))]
        public string UpdateChannel { get; set; } = UpdateChannelOption.Stable.ToString();

        public LabelItem ProjectUrl { get; set; } = new("https://github.com/sucooer/MediaInfoKeeper") {
            HyperLink = "https://github.com/sucooer/MediaInfoKeeper",
            Icon = IconNames.open_in_new
        };

        [DisplayName("版本信息")] public StatusItem VersionStatus { get; set; } = new("版本信息", "当前版本：未知\n最新版本：加载中");

        [DisplayName("更新说明")]
        [Description("始终显示全部 GitHub Releases 的发布记录；预发布版会额外标记为 [Prerelease]。")]
        public string ReleaseHistoryBody { get; set; } = "加载中";

        public void Initialize() {
            if (string.IsNullOrWhiteSpace(UpdateChannel)) UpdateChannel = UpdateChannelOption.Stable.ToString();

            UpdateChannelList.Clear();
            foreach (UpdateChannelOption item in Enum.GetValues(typeof(UpdateChannelOption)))
                UpdateChannelList.Add(new EditorSelectOption {
                    Name = item == UpdateChannelOption.Stable ? "Stable" : "Beta",
                    Value = item.ToString(),
                    IsEnabled = true
                });
        }

        public override IEditObjectContainer CreateEditContainer() {
            var container = (EditObjectContainer)base.CreateEditContainer();
            var root = container.EditorRoot;
            if (root?.EditorItems == null || root.EditorItems.Length == 0) return container;

            var items = new List<EditorBase>(root.EditorItems.Length);
            var itemLookup = new Dictionary<string, EditorBase>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in root.EditorItems) {
                var key = item.Name ?? item.Id;
                if (item is EditorText text)
                    if (string.Equals(key, nameof(ReleaseHistoryBody), StringComparison.OrdinalIgnoreCase)) {
                        text.IsReadOnly = true;
                        text.MultiLine = true;
                        text.LineCount = 12;
                        text.AllowEmpty = true;
                    }

                items.Add(item);
                if (!string.IsNullOrEmpty(key) && !itemLookup.ContainsKey(key)) itemLookup.Add(key, item);
            }

            var groupedItems = new List<EditorBase>();
            var groupIndex = 0;

            void AddGroup(string title, string description, params string[] propertyNames) {
                var groupItems = new List<EditorBase>();
                foreach (var propertyName in propertyNames)
                    if (itemLookup.TryGetValue(propertyName, out var item)) {
                        groupItems.Add(item);
                        itemLookup.Remove(propertyName);
                    }

                if (groupItems.Count == 0) return;

                groupIndex++;
                var group = new EditorGroup(title, groupItems.ToArray(), $"group{groupIndex}", root.Id, null) {
                    Description = description
                };
                groupedItems.Add(group);
            }

            AddGroup("GitHub", "",
                nameof(GitHubToken),
                nameof(DownloadUrlPrefix),
                nameof(UpdateChannel),
                nameof(ProjectUrl),
                nameof(VersionStatus),
                nameof(ReleaseHistoryBody));

            var remaining = new List<EditorBase>();
            foreach (var item in items) {
                var key = item.Name ?? item.Id;
                if (!string.IsNullOrEmpty(key) && itemLookup.ContainsKey(key)) {
                    remaining.Add(item);
                    itemLookup.Remove(key);
                }
            }

            if (remaining.Count > 0) {
                groupIndex++;
                groupedItems.Add(new EditorGroup("未分组", remaining.ToArray(), $"group{groupIndex}", root.Id, null));
            }

            root.EditorItems = groupedItems.Count > 0 ? groupedItems.ToArray() : items.ToArray();
            return container;
        }
    }
}
