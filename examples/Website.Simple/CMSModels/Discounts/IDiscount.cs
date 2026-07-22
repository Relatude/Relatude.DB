using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.Ecom.Models {
    public interface IDiscount
    {
            public string Code { get; set; }
            public decimal Amount { get; set; }
            public bool IsPercentage { get; set; }
            public bool Stackable { get; set; }
            public bool IsOrderLevelDiscount { get; set; }

            public string ExternalId { get; set; }
            public string CouponCode { get; set; }
            public bool Enabled { get; set; }
            public DateTime ValidFrom { get; set; }
            public DateTime ValidTo { get; set; }
            public int MaxNumberOfUses { get; set; }
            public int NumberOfUses { get; set; }
             public ShopDiscounts.Shop Shop { get; set; }
             public DiscountsOrders.Orders Orders { get; set; }
             public DiscountsProducts.Products Products { get; set; }
             
    }
}
