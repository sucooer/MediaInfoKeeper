using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace MediaInfoKeeper.Store {
    public class EmbeddedInfoStore {
        private const string DefaultImageMimeType = "image/jpeg";
        private static readonly System.Text.RegularExpressions.Regex TrailingYearRegex =
            new(@"\s*\((?:19|20)\d{2}\)$", System.Text.RegularExpressions.RegexOptions.Compiled);
        private readonly IJsonSerializer jsonSerializer;
        private readonly ILogger logger;

        public EmbeddedInfoStore(IJsonSerializer jsonSerializer) {
            this.jsonSerializer = jsonSerializer;
            logger = Plugin.SharedLogger;
        }

        public EmbeddedInfoSnapshot ReadFromFile(BaseItem item) {
            var snapshot = ReadDocuments(MediaInfoDocument.GetMediaInfoJsonPath(item)).FirstOrDefault()?.EmbeddedInfo;
            logger.Debug($"EmbeddedInfoStore 从文件读取音乐元数据: {item.FileName ?? item.Path} 是否存在={snapshot != null}");
            return snapshot;
        }

        public bool HasInFile(BaseItem item) {
            var hasInFile = ReadFromFile(item) != null;
            logger.Debug($"EmbeddedInfoStore 检查文件是否包含音乐元数据: {item.FileName ?? item.Path} 结果={hasInFile}");
            return hasInFile;
        }

        public bool WriteToFile(BaseItem item) {
            var mediaInfoJsonPath = MediaInfoDocument.GetMediaInfoJsonPath(item);
            var documents = ReadDocuments(mediaInfoJsonPath);
            var document = documents.FirstOrDefault() ?? new MediaInfoDocument();
            if (document.EmbeddedInfo != null) {
                logger.Debug($"EmbeddedInfoStore Json写入音乐元数据跳过: {item.FileName ?? item.Path}");
                return false;
            }

            var snapshot = CreateForPersist(item);
            if (snapshot == null) {
                logger.Debug($"EmbeddedInfoStore Json写入音乐元数据跳过: {item.FileName ?? item.Path} 非音频或无有效数据");
                return false;
            }

            document.EmbeddedInfo = snapshot;
            document.EmbeddedImage = GetPrimaryImageBase64(item as Audio);
            SaveDocuments(documents, document, mediaInfoJsonPath);
            logger.Debug($"EmbeddedInfoStore Json写入音乐元数据成功: {item.FileName ?? item.Path}");
            return true;
        }

        public void OverWriteToFile(BaseItem item) {
            var mediaInfoJsonPath = MediaInfoDocument.GetMediaInfoJsonPath(item);
            var documents = ReadDocuments(mediaInfoJsonPath);
            var document = documents.FirstOrDefault() ?? new MediaInfoDocument();
            document.EmbeddedInfo = CreateForPersist(item);
            var embeddedImage = GetPrimaryImageBase64(item as Audio);
            if (!string.IsNullOrWhiteSpace(embeddedImage)) document.EmbeddedImage = embeddedImage;

            SaveDocuments(documents, document, mediaInfoJsonPath);
            logger.Debug($"EmbeddedInfoStore 写入音乐元数据成功: {item.FileName ?? item.Path}");
        }

        public void OverWriteImageToFile(BaseItem item) {
            if (item is not Audio audio) return;

            var mediaInfoJsonPath = MediaInfoDocument.GetMediaInfoJsonPath(item);
            var documents = ReadDocuments(mediaInfoJsonPath);
            var document = documents.FirstOrDefault() ?? new MediaInfoDocument();
            var embeddedImage = GetPrimaryImageBase64(audio);
            if (string.IsNullOrWhiteSpace(embeddedImage)) return;

            document.EmbeddedImage = embeddedImage;
            SaveDocuments(documents, document, mediaInfoJsonPath);
            logger.Debug($"EmbeddedInfoStore 写入音乐主图成功: {item.FileName ?? item.Path}");
        }

        public MediaInfoDocument.MediaInfoRestoreResult ApplyToItem(BaseItem item) {
            if (item is not Audio audio) return MediaInfoDocument.MediaInfoRestoreResult.Failed;

            var snapshot = ReadFromFile(item);
            if (snapshot == null) {
                logger.Debug($"EmbeddedInfoStore 恢复音乐元数据失败: {item.FileName ?? item.Path} JSON 中无音乐元数据");
                return MediaInfoDocument.MediaInfoRestoreResult.Failed;
            }

            try {
                if (!string.IsNullOrWhiteSpace(snapshot.Name)) audio.Name = snapshot.Name;

                audio.AlbumArtists = snapshot.AlbumArtists ?? Array.Empty<string>();
                audio.Artists = snapshot.Artists ?? Array.Empty<string>();
                audio.Genres = snapshot.Genres ?? Array.Empty<string>();
                audio.IndexNumber = snapshot.IndexNumber;
                audio.ParentIndexNumber = snapshot.ParentIndexNumber;
                audio.ProductionYear = snapshot.ProductionYear;
                audio.Album = NormalizeAlbumValue(audio, snapshot.Album);
                audio.SetProviderIds(
                    new ProviderIdDictionary(snapshot.ProviderIds ?? new Dictionary<string, string>()));
                RestorePrimaryImage(audio, item);
                audio.UpdateToRepository(ItemUpdateType.MetadataImport);

                logger.Debug($"EmbeddedInfoStore 恢复音乐元数据完成: {item.FileName ?? item.Path}");
                return MediaInfoDocument.MediaInfoRestoreResult.Restored;
            }
            catch (Exception e) {
                logger.Error($"EmbeddedInfoStore 恢复音乐元数据失败: {item.FileName ?? item.Path}");
                logger.Error(e.Message);
                logger.Debug(e.StackTrace);
                return MediaInfoDocument.MediaInfoRestoreResult.Failed;
            }
        }

        /// <summary>
        ///     剥离专辑名尾部的 "(年份)" 后缀。
        ///     Emby 在元数据解析回退时会把文件夹名当专辑名写入条目，文件夹名带年份时
        ///     Album 会变成 "专辑 (2026)"。仅当括号内年份与条目年份一致、且能通过
        ///     内嵌标签或文件夹名回退特征确认时才剥离，避免误伤真正以年份结尾的专辑名。
        /// </summary>
        private string NormalizeAlbumValue(Audio audio, string candidate) {
            if (string.IsNullOrWhiteSpace(candidate)) return candidate;

            var match = TrailingYearRegex.Match(candidate);
            if (!match.Success) return candidate;

            var stripped = candidate.Substring(0, match.Index).TrimEnd();
            if (string.IsNullOrWhiteSpace(stripped)) return candidate;

            var yearText = candidate.Substring(match.Index + match.Length - 5, 4);
            if (!int.TryParse(yearText, out var suffixYear)) return candidate;

            if (audio.ProductionYear.HasValue && audio.ProductionYear.Value != suffixYear) return candidate;

            // 内嵌标签可读时以标签为准：标签等于剥离值，说明年份来自文件夹名。
            var tagAlbum = TryReadFlacAlbumTag(audio.Path);
            if (tagAlbum != null)
                return string.Equals(tagAlbum, stripped, StringComparison.Ordinal) ? stripped : candidate;

            // 标签不可读（非 FLAC 或解析失败）时，用文件夹名回退特征校验。
            var folderName = GetContainingFolderName(audio);
            if (string.Equals(folderName, candidate, StringComparison.OrdinalIgnoreCase)) {
                logger?.Info($"EmbeddedInfoStore 剥离专辑名年份后缀: {candidate} -> {stripped}");
                return stripped;
            }

            return candidate;
        }

        private static string GetContainingFolderName(BaseItem item) {
            var folderPath = item.ContainingFolderPath;
            if (string.IsNullOrWhiteSpace(folderPath)) return null;

            folderPath = folderPath.TrimEnd('\\', '/');
            var separatorIndex = folderPath.LastIndexOfAny(new[] { '\\', '/' });
            return separatorIndex < 0 ? folderPath : folderPath.Substring(separatorIndex + 1);
        }

        /// <summary>读取 FLAC 内嵌 Vorbis 注释中的 ALBUM 标签；非 FLAC 或读取失败返回 null。</summary>
        private static string TryReadFlacAlbumTag(string mediaPath) {
            try {
                if (string.IsNullOrWhiteSpace(mediaPath) ||
                    !mediaPath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(mediaPath))
                    return null;

                using var stream = new FileStream(mediaPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                Span<byte> header = stackalloc byte[4];
                if (stream.Read(header) < 4 || header[0] != (byte)'f' || header[1] != (byte)'L' ||
                    header[2] != (byte)'a' || header[3] != (byte)'C')
                    return null;

                Span<byte> blockHeader = stackalloc byte[4];
                while (stream.Read(blockHeader) == 4) {
                    var isLast = (blockHeader[0] & 0x80) != 0;
                    var blockType = blockHeader[0] & 0x7F;
                    var blockSize = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];

                    if (blockType == 4) {
                        var block = new byte[blockSize];
                        if (stream.Read(block) < blockSize) return null;

                        return ReadAlbumFromVorbisComment(block);
                    }

                    if (isLast) return null;

                    stream.Seek(blockSize, SeekOrigin.Current);
                }
            }
            catch {
                // best-effort: 标签读取失败时由文件夹名回退特征兜底。
            }

            return null;
        }

        private static string ReadAlbumFromVorbisComment(byte[] block) {
            if (block.Length < 8) return null;

            var pos = 4; // 跳过 vendor 长度前 4 字节中的高位读取起点
            var vendorLength = BitConverter.ToInt32(block, 0);
            if (vendorLength < 0 || vendorLength > block.Length) return null;

            pos = 4 + vendorLength;
            if (pos + 4 > block.Length) return null;

            var commentCount = BitConverter.ToInt32(block, pos);
            pos += 4;
            if (commentCount < 0 || commentCount > 10000) return null;

            for (var i = 0; i < commentCount; i++) {
                if (pos + 4 > block.Length) return null;

                var length = BitConverter.ToInt32(block, pos);
                pos += 4;
                if (length < 0 || pos + length > block.Length) return null;

                var comment = System.Text.Encoding.UTF8.GetString(block, pos, length);
                pos += length;

                var equalsIndex = comment.IndexOf('=');
                if (equalsIndex <= 0) continue;

                var key = comment.Substring(0, equalsIndex);
                if (!string.Equals(key, "ALBUM", StringComparison.OrdinalIgnoreCase)) continue;

                return comment.Substring(equalsIndex + 1);
            }

            return null;
        }

        private EmbeddedInfoSnapshot CreateForPersist(BaseItem item) {
            if (item is not Audio audio) return null;

            return new EmbeddedInfoSnapshot {
                Name = audio.Name,
                Album = NormalizeAlbumValue(audio, audio.Album),
                AlbumArtists = audio.AlbumArtists ?? Array.Empty<string>(),
                Artists = audio.Artists ?? Array.Empty<string>(),
                Genres = audio.Genres ?? Array.Empty<string>(),
                IndexNumber = audio.IndexNumber,
                ParentIndexNumber = audio.ParentIndexNumber,
                ProductionYear = audio.ProductionYear,
                ProviderIds = new Dictionary<string, string>(audio.ProviderIds ?? new ProviderIdDictionary(),
                    StringComparer.OrdinalIgnoreCase)
            };
        }

        private string GetPrimaryImageBase64(Audio audio) {
            if (audio == null) return null;

            var primaryImage = audio.GetImageInfo(ImageType.Primary, 0);
            var imagePath = primaryImage?.Path;
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) {
                logger.Debug($"EmbeddedInfoStore 主图写入跳过: {audio.FileName ?? audio.Path} 无可读取主图");
                return null;
            }

            try {
                var base64 = Convert.ToBase64String(File.ReadAllBytes(imagePath));
                logger.Debug($"EmbeddedInfoStore 主图写入成功: {audio.FileName ?? audio.Path}");
                return base64;
            }
            catch (Exception ex) {
                logger.Warn($"EmbeddedInfoStore 主图写入失败: {audio.FileName ?? audio.Path} {ex.Message}");
                return null;
            }
        }

        private void RestorePrimaryImage(Audio audio, BaseItem item) {
            if (audio.HasImage(ImageType.Primary)) return;

            var document = ReadDocuments(MediaInfoDocument.GetMediaInfoJsonPath(item)).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(document?.EmbeddedImage)) return;

            try {
                var imageBytes = Convert.FromBase64String(document.EmbeddedImage);
                var mimeType = DetectImageMimeType(imageBytes);
                using var imageStream = new MemoryStream(imageBytes, false);
                var libraryOptions = Plugin.LibraryManager.GetLibraryOptions(audio);
                Plugin.ProviderManager.SaveImage(
                        audio,
                        libraryOptions,
                        imageStream,
                        mimeType.AsMemory(),
                        ImageType.Primary,
                        null,
                        Array.Empty<long>(),
                        new DirectoryService(logger, Plugin.FileSystem),
                        true,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                logger.Info($"EmbeddedInfoStore 主图恢复成功: {audio.FileName ?? audio.Path}");
            }
            catch (Exception ex) {
                logger.Warn($"EmbeddedInfoStore 主图恢复失败: {audio.FileName ?? audio.Path} {ex.Message}");
            }
        }

        private static string DetectImageMimeType(byte[] imageBytes) {
            if (imageBytes == null || imageBytes.Length < 4) return DefaultImageMimeType;

            if (imageBytes.Length >= 3 &&
                imageBytes[0] == 0xFF &&
                imageBytes[1] == 0xD8 &&
                imageBytes[2] == 0xFF)
                return DefaultImageMimeType;

            if (imageBytes.Length >= 8 &&
                imageBytes[0] == 0x89 &&
                imageBytes[1] == 0x50 &&
                imageBytes[2] == 0x4E &&
                imageBytes[3] == 0x47 &&
                imageBytes[4] == 0x0D &&
                imageBytes[5] == 0x0A &&
                imageBytes[6] == 0x1A &&
                imageBytes[7] == 0x0A)
                return "image/png";

            if (imageBytes.Length >= 6 &&
                imageBytes[0] == 0x47 &&
                imageBytes[1] == 0x49 &&
                imageBytes[2] == 0x46)
                return "image/gif";

            if (imageBytes.Length >= 2 &&
                imageBytes[0] == 0x42 &&
                imageBytes[1] == 0x4D)
                return "image/bmp";

            if (imageBytes.Length >= 12 &&
                imageBytes[0] == 0x52 &&
                imageBytes[1] == 0x49 &&
                imageBytes[2] == 0x46 &&
                imageBytes[3] == 0x46 &&
                imageBytes[8] == 0x57 &&
                imageBytes[9] == 0x45 &&
                imageBytes[10] == 0x42 &&
                imageBytes[11] == 0x50)
                return "image/webp";

            return DefaultImageMimeType;
        }

        private void SaveDocuments(List<MediaInfoDocument> documents, MediaInfoDocument document,
            string mediaInfoJsonPath) {
            if (documents.Count == 0) documents.Add(document);

            Directory.CreateDirectory(Path.GetDirectoryName(mediaInfoJsonPath));
            jsonSerializer.SerializeToFile(documents, mediaInfoJsonPath);
        }

        private List<MediaInfoDocument> ReadDocuments(string mediaInfoJsonPath) {
            try {
                return jsonSerializer.DeserializeFromFile<List<MediaInfoDocument>>(mediaInfoJsonPath) ??
                       new List<MediaInfoDocument>();
            }
            catch {
                return new List<MediaInfoDocument>();
            }
        }
    }
}
