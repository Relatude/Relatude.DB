namespace Relatude.DB.FileConversion;

// The numbers of the two enums below are part of conversion cache keys and of encoded asset URLs,
// so they are fixed: add new members with the next free number, never renumber (see FileFormat).
public enum ImageCropMode {
    Fill = 0, // Resize and crop to fill the target dimensions. This may cut off parts of the image if the aspect ratio doesn't match.
    Fit = 1, // Resize to fit within the target dimensions while preserving aspect ratio. This may result in letterboxing (empty space) if the aspect ratio doesn't match.
    Stretch = 2, // Resize to fill the target dimensions without preserving aspect ratio. This may distort the image if the aspect ratio doesn't match.
    Auto = 3, // Automatically choose between Fill and Fit based on the content of the image. For example, if the image has a mostly uniform colored edge, it should use Fit.
}
public enum AutoLightDarkSwitch {
    None = 0, // no change
    AdaptToLightModeIfNeeded = 1, // invert the luminance if the image looks made for a dark surface
    AdaptToDarkModeIfNeeded = 2, // invert the luminance if the image looks made for a light surface
}
