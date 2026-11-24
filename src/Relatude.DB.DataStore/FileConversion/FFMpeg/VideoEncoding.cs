using System.Globalization;
using Relatude.DB.Common;

namespace Relatude.DB.FileConversion.FFMpeg;

/// <summary>
/// ffmpeg's arguments for a video converted to another video (<see cref="FileAdjustmentVideo"/>), the same for every
/// converter. An MP4 is written as every browser plays it: 8-bit with its colour at a quarter of the pixels (yuv420p),
/// as ffmpeg otherwise keeps a 10-bit or 4:2:2 source's colour and writes an H.264 profile no browser decodes (an
/// iPhone's HDR clips, ProRes), and with its index at the start (faststart), so playing starts before the end of the file
/// is fetched.
/// </summary>
public static class VideoEncoding {
    public static List<string> ArgumentsFor(FileAdjustmentVideo? adjustment, string input, string output) {
        List<string> args = ["-i", input];
        // most video encoders take even sizes only: a size asked for is rounded up, and -2 keeps the other side even
        if (adjustment?.Width.HasValue == true || adjustment?.Height.HasValue == true) {
            var width = adjustment.Width is { } w ? w + w % 2 : -2;
            var height = adjustment.Height is { } h ? h + h % 2 : -2;
            args.AddRange(["-vf", string.Create(CultureInfo.InvariantCulture, $"scale={width}:{height}")]);
        }
        if (adjustment?.TargetBitRateInMbps > 0)
            args.AddRange(["-b:v", ((int)(adjustment.TargetBitRateInMbps * 1024)).ToString(CultureInfo.InvariantCulture) + "k"]);
        if (FileFormatUtil.GetDetailedFormat(output) == FileFormat.Mp4)
            args.AddRange(["-pix_fmt", "yuv420p", "-movflags", "+faststart"]);
        args.Add(output);
        return args;
    }
}
