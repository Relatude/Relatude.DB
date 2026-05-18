using Relatude.DB.FileConverter;

namespace Relatude.DB.FileConversion.Images;

internal interface IImageConverter {
    Stream Convert(Stream input, FileAdjustmentImage adjustments);
}
