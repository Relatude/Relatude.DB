namespace Relatude.DB.Datamodels.Properties;
/// <summary>
/// Where an editing interface puts a property's title (its label) beside its control. It says nothing to the store.
/// Left out (0 on an attribute, null on the model), it is <see cref="Default"/>.
/// </summary>
public enum TitleLayout {
    /// <summary>
    /// What suits the control, as the editing interface decides: a CMS puts the title to the left of most controls and
    /// above a text area and an HTML field. What a property is when nothing is said, on the attribute or the model.
    /// </summary>
    Default = 0,
    /// <summary>To the left of the control, on the same line.</summary>
    Left = 1,
    /// <summary>Above the control.</summary>
    Above = 2,
    /// <summary>No title: the control alone.</summary>
    None = 3,
}
