using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.CMS.Models {
    public interface ITemplate :IItem {
        public string Controller {  get; set; }
        public string Action { get; set; }        
        public string PagePath { get; set; }
        
        TemplatePages.Pages Pages { get; set; }

    }
}
