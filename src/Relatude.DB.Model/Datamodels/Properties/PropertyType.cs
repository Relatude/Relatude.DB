namespace Relatude.DB.Datamodels.Properties;

public enum PropertyType : int {
    Any = 0,

    Boolean = 1,
    Integer = 2,
    String = 3,
    StringArray = 4,
    Double = 5,
    Float = 6,
    Decimal = 7,
    DateTime = 8,
    TimeSpan = 9,
    Guid = 10,
    Long = 11,
    ByteArray = 12,
    File = 13,
    FloatArray = 14,
    DateTimeOffset = 15,

    Relation = 100,
    //Collection = 200,
    //DataObject = 201,
    //FacetCollection = 202,
    //FacetNumberRange = 203,

    //Empty,
    //Guid,
    //Relation,
    //Decimal,
    //WordList,
    //DateTime,
    //GeoCoordinate,
    //GuidSet,
    //IntegerSet,
    //ShortStringSet,
}
