using System.Diagnostics;
using Memtly.Core.Enums;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Localization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace Memtly.Core.Helpers
{
    public interface IImageHelper
    {
        Task<bool> GenerateThumbnail(string filePath, string savePath, int size = 720);
        Task<ImageOrientation> GetOrientation(string path);
        ImageOrientation GetOrientation(Image img);
        MediaType GetMediaType(string filePath);
        DateTime? GetExifCreationDateTaken(string path);
        Task<bool> DownloadFFMPEG(string path);
    }

    public class ImageHelper : IImageHelper
    {
        private readonly IFileHelper _fileHelper;
        private readonly ILogger _logger;
        private readonly IStringLocalizer<Localization.Translations> _localizer;

        private static bool FfmpegInstalled = false;
        private static string? FfmpegDirectory = null;

        public ImageHelper(IFileHelper fileHelper, ILogger<ImageHelper> logger, IStringLocalizer<Localization.Translations> localizer)
        {
            _fileHelper = fileHelper;
            _logger = logger;
            _localizer = localizer;
        }

        public async Task<bool> GenerateThumbnail(string filePath, string savePath, int size = 720)
        {
            if (_fileHelper.FileExists(filePath))
            { 
                try
                {
                    var mediaType = GetMediaType(filePath);
                    if (mediaType == MediaType.Image || mediaType == MediaType.Video)
                    {
                        var filename = Path.GetFileName(filePath);
                        string? tempFrame = null;

                        if (mediaType == MediaType.Video)
                        {
                            // Xabe.FFmpeg's executable discovery is unreliable on some platforms
                            // (it fails to validate a present, working ffmpeg), so shell out to
                            // ffmpeg directly to grab the first frame as a PNG that ImageSharp
                            // can then resize into the thumbnail.
                            tempFrame = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
                            if (!await ExtractVideoFrame(filePath, tempFrame))
                            {
                                return false;
                            }
                            filePath = tempFrame;
                        }

                        try
                        {
                            using (var img = await Image.LoadAsync(filePath))
                            {
                                var width = 0;
                                var height = 0;

                                var orientation = this.GetOrientation(img);
                                if (orientation == ImageOrientation.Square)
                                {
                                    width = size;
                                    height = size;
                                }
                                else if (orientation == ImageOrientation.Landscape)
                                {
                                    var scale = (decimal)size / (decimal)img.Width;
                                    width = (int)((decimal)img.Width * scale);
                                    height = (int)((decimal)img.Height * scale);
                                }
                                else if (orientation == ImageOrientation.Portrait)
                                {
                                    var scale = (decimal)size / (decimal)img.Height;
                                    width = (int)((decimal)img.Width * scale);
                                    height = (int)((decimal)img.Height * scale);
                                }

                                img.Mutate(x =>
                                {
                                    x.Resize(width, height);
                                    x.AutoOrient();
                                });

                                await img.SaveAsWebpAsync(savePath);
                            }
                        }
                        finally
                        {
                            if (tempFrame != null)
                            {
                                _fileHelper.DeleteFileIfExists(tempFrame);
                            }
                        }
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to generate thumbnail - '{filePath}'");
                }
            }

            return false;
        }

        private async Task<bool> ExtractVideoFrame(string videoPath, string savePath)
        {
            try
            {
                var ffmpeg = string.IsNullOrWhiteSpace(FfmpegDirectory) ? "ffmpeg" : Path.Combine(FfmpegDirectory, "ffmpeg");
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-ss");
                psi.ArgumentList.Add("0");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(videoPath);
                psi.ArgumentList.Add("-frames:v");
                psi.ArgumentList.Add("1");
                psi.ArgumentList.Add(savePath);

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        _logger.LogWarning($"Failed to start ffmpeg for video snapshot - '{videoPath}'");
                        return false;
                    }

                    var stderr = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();

                    if (process.ExitCode != 0 || !_fileHelper.FileExists(savePath))
                    {
                        _logger.LogWarning($"ffmpeg failed to snapshot video - '{videoPath}' (exit {process.ExitCode}): {stderr}");
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to snapshot video - '{videoPath}'");
                return false;
            }
        }

        public MediaType GetMediaType(string path)
        {
            try
            {
                var provider = new FileExtensionContentTypeProvider();
                if (provider.TryGetContentType(path, out string? contentType))
                {
                    if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        return MediaType.Image;
                    }
                    else if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                    {
                        return MediaType.Video;
                    }
                }
            }
            catch { }
                
            return MediaType.Unknown;
        }

        public async Task<ImageOrientation> GetOrientation(string path)
        {
            var orientation = ImageOrientation.Unknown;

            if (_fileHelper.FileExists(path))
            {
                try
                {
                    using (var img = await Image.LoadAsync(path))
                    {
                        orientation = this.GetOrientation(img);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to get image orientation- '{path}'");
                }
            }

            return orientation;
        }

        public ImageOrientation GetOrientation(Image img)
        {
            if (img != null)
            {
                if (img.Width > img.Height)
                {
                    return ImageOrientation.Landscape;
                }
                else if (img.Width < img.Height)
                {
                    return ImageOrientation.Portrait;
                }
                else if (img.Width == img.Height)
                {
                    return ImageOrientation.Square;
                }
            }

            return ImageOrientation.Unknown;
        }

        public DateTime? GetExifCreationDateTaken(string path)
        {
            try
            {
                var directories = ImageMetadataReader.ReadMetadata(path);

                var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
                if (subIfd?.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dateOriginal) == true)
                { 
                    return dateOriginal;
                }

                var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
                if (ifd0?.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dateTime) == true)
                { 
                    return dateTime;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to get image EXIF creation datetime - '{path}'");
            }

            return null;
        }

        public async Task<bool> DownloadFFMPEG(string path)
        {
            try
            {
                if (!_fileHelper.DirectoryExists(path))
                {
                    _fileHelper.CreateDirectoryIfNotExists(path);
                    await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, path);
                }

                FFmpeg.SetExecutablesPath(path);
                FfmpegDirectory = path;
                FfmpegInstalled = true;

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to download FFmpeg - '{path}'");
            }

            return false;
        }
    }
}