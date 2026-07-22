using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.Ecom.Models {
    public interface IShippingMethod
    {        
        public string ProviderKey { get;set; }
         public ShopsShippingMethods.Shops Shops { get; set; }
         public ShippingMethodOrders.Orders Orders { get; set; }

    }
}
