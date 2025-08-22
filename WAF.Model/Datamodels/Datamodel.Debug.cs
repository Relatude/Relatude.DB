using WAF.Datamodels.Properties;
using System.Text;

namespace WAF.Datamodels;

public partial class Datamodel {
    public string DebugString {
        get {
            var builder = new StringBuilder();

            builder.AppendLine("------------------------------------NodeTypes------------------------------------");
            foreach (var (nGuid, nModel) in NodeTypes) {
                builder.AppendLine($"{(nModel.FullName + ":").PadRight(120, ' ')}({nGuid})");
                foreach (var (pGuid, pModel) in nModel.Properties) {
                    string propertyType;
                    if (pModel is RelationPropertyModel rModel) {
                        if (rModel.RelationValueType == RelationValueType.NotRelevant)
                            propertyType = rModel.GetFullNameOfRelated(this);
                        else
                            propertyType = rModel.GetFullNameOfRelated(this) + "[]";
                    } else {
                        propertyType = pModel.PropertyType.ToString();
                    }

                    builder.AppendLine($"{$"    {pModel.CodeName}: {propertyType}".PadRight(120)}({pGuid})");
                }

                builder.AppendLine();
            }

            builder.AppendLine();
            builder.AppendLine("------------------------------------Relations------------------------------------");
            foreach (var (rGuid, rModel) in Relations) {
                builder.AppendLine($"{(rModel + ":").PadRight(120, ' ')}({rGuid})");
                var sourceTypes = String.Join(" | ", rModel.SourceTypes.Select(guid => NodeTypes[guid].FullName));
                var targetTypes = String.Join(" | ", rModel.TargetTypes.Select(guid => NodeTypes[guid].FullName));

                string infoStr = rModel.RelationType switch {
                    RelationType.OneOne => $"One {sourceTypes} One {targetTypes}",
                    RelationType.OneToOne => $"One {sourceTypes} to One {targetTypes}",
                    RelationType.OneToMany => $"One {sourceTypes} to Many {targetTypes}",
                    RelationType.ManyMany => $"Many {sourceTypes} Many {targetTypes}",
                    RelationType.ManyToMany => $"Many {sourceTypes} to Many {targetTypes}",
                    _ => "ERROR"
                };

                var sourceGuids = String.Join('|', rModel.SourceTypes);
                var targetGuids = String.Join('|', rModel.TargetTypes);
                builder.AppendLine($"{$"    {infoStr}".PadRight(120, ' ')}({sourceGuids} <-> {targetGuids})");
                builder.AppendLine();
            }

            builder.AppendLine();
            builder.AppendLine("------------------------------------Properties------------------------------------");
            foreach (var (pGuid, pModel) in Properties) {
                string propertyType;
                if (pModel is RelationPropertyModel rModel) {
                    if (rModel.RelationValueType == RelationValueType.NotRelevant)
                        propertyType = rModel.GetFullNameOfRelated(this);
                    else
                        propertyType = rModel.GetFullNameOfRelated(this) + "[]";
                } else {
                    propertyType = pModel.PropertyType.ToString();
                }

                builder.AppendLine($"{$"    {pModel.GetFullName(this)}: {propertyType}".PadRight(120)}({pGuid})");
            }

            return builder.ToString();
        }
    }

}