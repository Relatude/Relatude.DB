using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.Ecom.Models {
    public interface IPaymentMethod {
        
        public string ProviderKey { get;set; }
         public ShopsPaymentMethods.Shops Shops { get; set; }
         public PaymentMethodOrders.Orders Orders { get; set; }
    }
}
