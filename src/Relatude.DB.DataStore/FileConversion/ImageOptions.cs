// ImageCropMode is in Relatude.DB.Common (ImageCropMode.cs), in this namespace still, since a picture's meta holds one
// (ImageFileMeta.CropMode); the forward keeps code built against this assembly working.
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(Relatude.DB.FileConversion.ImageCropMode))]

namespace Relatude.DB.FileConversion;

// The numbers of the enum below are part of conversion cache keys and of encoded asset URLs,
// so they are fixed: add new members with the next free number, never renumber (see FileFormat).
public enum AutoLightDarkSwitch {
    None = 0, // no change
    AdaptToLightModeIfNeeded = 1, // invert the luminance if the image looks made for a dark surface
    AdaptToDarkModeIfNeeded = 2, // invert the luminance if the image looks made for a light surface
}
