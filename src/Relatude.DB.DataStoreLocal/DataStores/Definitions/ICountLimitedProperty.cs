namespace Relatude.DB.DataStores.Definitions;

internal interface ICountLimitedProperty {
    int MaxCount { get; }
    int CountOf(object? value);
}
