
using Relatude.DB.Nodes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.CMS.Models {
    public interface IPage : IItem {        
        string MetaTitle { get; set; }
        string MetaDescription { get; set; }
        string MetaKeywords { get; set; }      
        
        TemplatePages.Template Template { get; set; }
    }
}
