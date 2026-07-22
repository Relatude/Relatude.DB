using Relatude.DB.Datamodels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.CMS.Models {
    public interface IItem {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public NodeMeta Meta { get; set; }       

    }
}
