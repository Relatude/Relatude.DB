using Relatude.DB.Nodes;

namespace Relatude.CMS.Models {
    public class TemplatePages : OneToMany<ITemplate, IPage> {
        public class Template : One { }
        public class Pages : Many { }
    }
}

