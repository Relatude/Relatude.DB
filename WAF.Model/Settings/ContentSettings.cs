using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
namespace WAF.Settings;
/// <summary>
/// Configurations relating to the content 
/// Like cultures, default values, access groups, macro etc. 
/// The Datamodel object should be content independent.
/// Content settings may also be used to override some settings in the datamodel
/// </summary>
public class ContentSettings {
    public List<CultureSetting> Cultures { get; set; } = new();
    public List<NodeSettings> Nodes { get; set; } = new();
    public List<RelationSettings> Relations { get; set; } = new();
    public string ToJson() => JsonSerializer.Serialize(this);
    public static ContentSettings FromJson(string json) {
        if (string.IsNullOrEmpty(json)) return new();
        return JsonSerializer.Deserialize<ContentSettings>(json)
            ?? throw new Exception("Could not deserialize content settings. ");
    }
    public void EnsureSufficientDefaults() {
        if (Cultures.Count == 0) {
            Cultures.Add(new CultureSetting() { Id = Guid.Empty, LCID = 1033, Name = "English" });
        }
    }
    public void Validate() {
        if (Cultures.Count == 0) throw new Exception("There must be at least one culture. ");
        if (Cultures.Select(c => c.Id).Distinct().Count() != Cultures.Count) throw new Exception("Duplicate culture ids. ");
        if (Cultures.Select(c => c.LCID).Distinct().Count() != Cultures.Count) throw new Exception("Duplicate culture LCIDs. ");
        if (Cultures.Where(c => c.Id == Guid.Empty).Count() > 1) throw new Exception("There can only be one culture with an empty Id. ");
    }
}
public class CultureSetting {
    public Guid Id { get; set; }
    public int LCID { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FilePrefix() {
        if (Id == Guid.Empty) return "";
        return Id.ToString().Replace("-", "").ToLower();
    }
}
public class NodeSettings {
    public Guid Id { get; set; }
    public List<PropertySettings> Properties { get; set; } = new();
}
public class RelationSettings {
    public Guid Id { get; set; }
}
public class PropertySettings {
    public Guid Id { get; set; }
}
