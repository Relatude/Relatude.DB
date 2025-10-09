﻿using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;
using Relatude.DB.Datamodels;
using Relatude.DB.Transactions;
namespace Relatude.DB.DataStores.Definitions.PropertyTypes;
internal class StringProperty : Property, IPropertyContainsValue {
    SetRegister _sets;
    public StringProperty(StringPropertyModel pm, Definition def) : base(pm, def) {
        _isSystemTextIndexPropertyId = pm.Id == NodeConstants.SystemTextIndexPropertyId;
        PrefixSearch = pm.PrefixSearch;
        DefaultValue = pm.DefaultValue ?? string.Empty;
        _sets = def.Sets;
        InfixSearch = pm.InfixSearch;
        IndexedByWords = pm.IndexedByWords;
        IndexedBySemantic = pm.IndexedBySemantic;
        PropertyIdForVectors = pm.PropertyIdForEmbeddings;
        MinLength = pm.MinLength;
        MaxLength = pm.MaxLength;
        MinWordLength = pm.MinWordLength;
        MaxWordLength = pm.MaxWordLength;
        RegularExpression = pm.RegularExpression;
        IgnoreDuplicateEmptyValues = pm.IgnoreDuplicateEmptyValues;
    }
    internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, IAIProvider? ai) {
        if (Indexed) Index = IndexFactory.CreateValueIndex(store, def.Sets, this, null, write, read);
        if (IndexedByWords) WordIndex = IndexFactory.CreateWordIndex(store, def.Sets, this);
        //WordIndex = new WordIndex(def.Sets, Id + nameof(WordIndex), Id, MinWordLength, MaxWordLength, PrefixSearch, InfixSearch);
        if (Index != null) Indexes.Add(Index);
        if (WordIndex != null) Indexes.Add(WordIndex);
    }
    void write(string v, IAppendStream stream) => stream.WriteString(v);
    string read(IReadStream stream) => stream.ReadString();
    public override bool TryReorder(IdSet unsorted, bool descending, [MaybeNullWhen(false)] out IdSet sorted) {
        if (Index != null) {
            sorted = Index.ReOrder(unsorted, descending);
            return true;
        }
        return base.TryReorder(unsorted, descending, out sorted);
    }
    readonly public string DefaultValue;
    readonly public int MinLength = 0;
    readonly public int MaxLength = int.MaxValue;
    readonly public StringValueType StringType = StringValueType.AnyString;
    readonly public bool PrefixSearch;
    readonly public bool InfixSearch;
    readonly public bool IndexedByWords;
    readonly public bool IndexedBySemantic;
    readonly public Guid PropertyIdForVectors = Guid.Empty;
    readonly public bool IgnoreDuplicateEmptyValues;
    readonly public int MinWordLength = 3;
    readonly public int MaxWordLength = 30;
    readonly bool _isSystemTextIndexPropertyId;
    private string? _regularExpression;
    private Regex? _regEx;
    public string? RegularExpression {
        get => _regularExpression;
        private set {
            _regularExpression = value;
            _regEx = string.IsNullOrEmpty(_regularExpression) ? null : new Regex(_regularExpression);
        }
    }
    public override PropertyType PropertyType => PropertyType.String;
    public IValueIndex<string>? Index;
    public IWordIndex? WordIndex;
    public override object ForceValueType(object value, out bool changed) {
        return StringPropertyModel.ForceValueType(value, out changed);
    }
    public override void ValidateValue(object value) {
        var v = (string)value;
        if (v.Length > MaxLength) throw new Exception("String value is longer than maximum value allowed. ");
        if (v.Length < MinLength) throw new Exception("String value is shorter than minimum value allowed. ");
        if (_regEx != null && !_regEx.Match(v).Success) throw new Exception("Value does not match regular expression. ");
    }
    public override IRangeIndex? ValueIndex => Index;
    public override object GetDefaultValue() => DefaultValue;
    public bool ContainsValue(object value) {
        if (Index == null) throw new Exception("Index is null. ");
        return Index.ContainsValue((string)value);
    }
    public override bool CanBeFacet() => Indexed;
    public override Facets GetDefaultFacets(Facets? given) {
        if (Index == null) throw new NullReferenceException("Index is null. ");
        var facets = new Facets(Model);
        if (given?.DisplayName != null) facets.DisplayName = given.DisplayName;
        facets.IsRangeFacet = false;
        if (given != null && given.HasValues()) {
            foreach (var f in given.Values) facets.AddValue(new FacetValue(f.Value, f.Value2, f.DisplayName));
        } else {
            var possibleValues = Index.UniqueValues;
            foreach (var value in possibleValues) facets.AddValue(new FacetValue(value));
        }
        return facets;
    }
    public override void CountFacets(IdSet nodeIds, Facets facets) {
        if (Index == null) throw new NullReferenceException("Index is null. ");
        foreach (var facetValue in facets.Values) {
            var v = StringPropertyModel.ForceValueType(facetValue.Value, out _);
            facetValue.Count = Index.CountEqual(nodeIds, v);
        }
    }
    public override IdSet FilterFacets(Facets facets, IdSet nodeIds) {
        if (Index == null) throw new NullReferenceException("Index is null. ");
        List<string> selectedValues = new();
        foreach (var facetValue in facets.Values) {
            var v = StringPropertyModel.ForceValueType(facetValue.Value, out _);
            if (facetValue.Selected) selectedValues.Add(v);
        }
        if (selectedValues.Count > 0) nodeIds = Index.FilterInValues(nodeIds, selectedValues);
        return nodeIds;
    }
    SemanticIndex? tryGetSemanticIndex(DataStoreLocal db) {
        if (db._ai != null && IndexedBySemantic) {
            if (!db._definition.Properties.TryGetValue(this.PropertyIdForVectors, out var semProp))
                throw new Exception("Semantic property for vectors is not defined. ");
            if (semProp is FloatArrayProperty fa) {
                if (!fa.Indexed) throw new Exception("Semantic property " + semProp.CodeName + " is not indexed. ");
                if (fa.Index == null) throw new NullReferenceException("Semantic index is null. ");
                return fa.Index;
            } else {
                throw new Exception("Property for semantic index is not a SemanticProperty. ");
            }
        }
        return null;
    }
    public IdSet SearchForIdSet(string search, double ratioSemantic, float minimumVectorSimilarity, bool orSearch, int maxWordsEval, DataStoreLocal db) {
        SemanticIndex? semanticIndex = tryGetSemanticIndex(db);
        var textSearches = TermSet.Parse(search, MinWordLength, MaxWordLength);
        if (IndexedByWords && IndexedBySemantic && ratioSemantic < 1 && ratioSemantic > 0) {
            var wordHits = WordIndex == null ? IdSet.Empty : WordIndex.SearchForIdSetUnranked(textSearches, orSearch, maxWordsEval);
            var sematicHits = semanticIndex == null ? IdSet.Empty : semanticIndex.SearchForIdSetUnranked(search, minimumVectorSimilarity);
            return _sets.Union(wordHits, sematicHits);
        } else if (IndexedByWords && (ratioSemantic < 1 || !IndexedBySemantic)) {
            if (WordIndex == null) throw new NullReferenceException(nameof(WordIndex));
            return WordIndex.SearchForIdSetUnranked(textSearches, orSearch, maxWordsEval);
        } else if (IndexedBySemantic && (ratioSemantic > 0 || !IndexedByWords)) {
            if (semanticIndex == null) throw new NullReferenceException(nameof(SemanticIndex));
            return semanticIndex.SearchForIdSetUnranked(search, minimumVectorSimilarity);
        } else {
            return IdSet.Empty;
        }
    }
    internal IEnumerable<RawSearchHit> SearchForRankedHitData(IdSet baseSet, string search, double ratioSemantic, float minimumVectorSimilarity, bool orSearch, int pageIndex, int pageSize, int maxHitsEvaluated, int maxWordsEvaluated, DataStoreLocal db, out int totalHits) {
        // optimization via Reg cache us possible here....
        SemanticIndex? semanticIndex = tryGetSemanticIndex(db);
        var textSearches = TermSet.Parse(search, MinWordLength, MaxWordLength);
        if (IndexedByWords && IndexedBySemantic && ratioSemantic < 1 && ratioSemantic > 0) {
            if (WordIndex == null) throw new NullReferenceException(nameof(WordIndex));
            if (semanticIndex == null) throw new NullReferenceException(nameof(SemanticIndex));
            var top = (pageIndex + 1) * pageSize;
            var wordHits = WordIndex.SearchForRankedHitData(textSearches, 0, top, maxHitsEvaluated, maxWordsEvaluated, orSearch, out totalHits);
            var sematicHits = semanticIndex.SearchForHitData(search, top, minimumVectorSimilarity);
            var nodeIdsByWordHit = wordHits.Select(h => h.NodeId).ToHashSet();
            foreach (var h in sematicHits) {
                if (!nodeIdsByWordHit.Contains(h.NodeId) && baseSet.Has(h.NodeId)) {
                    wordHits.Add(h);
                }
            }
            return wordHits;
        } else if (IndexedByWords && (ratioSemantic < 1 || !IndexedBySemantic)) {
            if (WordIndex == null) throw new NullReferenceException(nameof(WordIndex));
            return WordIndex.SearchForRankedHitData(textSearches, pageIndex, pageSize, maxHitsEvaluated, maxWordsEvaluated, orSearch, out totalHits)
                .Where(h => baseSet.Has(h.NodeId));
        } else if (IndexedBySemantic && (ratioSemantic > 0 || !IndexedByWords)) {
            if (semanticIndex == null) throw new NullReferenceException(nameof(SemanticIndex));
            var top = (pageIndex + 1) * pageSize;
            var result = semanticIndex.SearchForHitData(search, top, minimumVectorSimilarity).Where(h => baseSet.Has(h.NodeId));
            totalHits = result.Count();
            return result;
        } else {
            totalHits = 0;
            return [];
        }
    }
    internal TextSample GetTextSample(TermSet search, string sourceText, int maxLength) {
        return new TextSample(search, sourceText, maxLength);
    }
    internal string GetSemanticSample(string search, string sourceText, DataStoreLocal db) {
        SemanticIndex? semanticIndex = tryGetSemanticIndex(db);
        return semanticIndex!.GetSample(search, sourceText);
    }
    internal string GetSemanticContextText(string question, string sourceText, DataStoreLocal db) {
        SemanticIndex? semanticIndex = tryGetSemanticIndex(db);
        return semanticIndex!.GetContextText(question, sourceText);
    }
    internal string GetWordContextText(string question, string sourceText) {
        return sourceText;
    }
    override public bool IsNodeRelevantForIndex(INodeData node, IIndex index) {
        // special handling for system text index property, allowing different node types to be indexed or not
        if (!_isSystemTextIndexPropertyId) return true;
        if (index is DataStores.Indexes.SemanticIndex) {
            return Definition.NodeTypes[node.NodeType].Model.SemanticIndex!.Value;
        }
        if (index is DataStores.Indexes.IWordIndex) {
            return Definition.NodeTypes[node.NodeType].Model.TextIndex!.Value;
        }
        throw new Exception("Internal text index property should only be indexed by semantic or word index. ");
    }
    public override bool SatisfyValueRequirement(object value1, object value2, ValueRequirement requirement) {
        var v1 = StringPropertyModel.ForceValueType(value1, out _);
        var v2 = StringPropertyModel.ForceValueType(value2, out _);
        return requirement switch {
            ValueRequirement.Equal => v1 == v2,
            ValueRequirement.NotEqual => v1 != v2,
            _ => throw new NotImplementedException(),
        };
    }
    public override bool AreValuesEqual(object v1, object v2) {
        if (v1 is string s1 && v2 is string s2) return s1 == s2;
        return false;
    }
}
