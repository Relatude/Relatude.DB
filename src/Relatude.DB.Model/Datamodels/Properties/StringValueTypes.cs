namespace Relatude.DB.Datamodels.Properties;
/// <summary>
/// What kind of text a string property holds, for editors and indexing. New values go at the end: the model files
/// name them, but compiled code may hold the number.
/// </summary>
public enum StringValueType:int {
    /// <summary>Any text. An editing interface chooses a one-line or a multi-line field itself, by the property's name.</summary>
    AnyString,
    HTML,
    Markdown,
    JSON,
    JavaScript,
    TypeScript,
    CSharp,
    CSS,
    /// <summary>Plain text on one line: an editing interface shows a text box. Stored and indexed as <see cref="AnyString"/>.</summary>
    SingleLineText,
    /// <summary>
    /// Plain text of several lines: an editing interface shows a text area, as many lines high as
    /// <see cref="StringPropertyModel.TextLines"/> says. Stored and indexed as <see cref="AnyString"/>.
    /// </summary>
    MultiLineText,
    /// <summary>
    /// Source code in any language: an editing interface shows a code editor. Which language it is the value does not
    /// say; the Relatude CMS highlights it in the one a string property beside it names (Language). Stored and indexed
    /// as <see cref="AnyString"/>.
    /// </summary>
    Code,
}
