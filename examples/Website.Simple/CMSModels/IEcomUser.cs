using Relatude.CMS.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.Ecom.Models {
    public interface IEcomUser :ICMSUser{
        public EcomUserOrders.Orders Orders { get; }

    }
}
