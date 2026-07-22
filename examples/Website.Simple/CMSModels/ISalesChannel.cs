using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.Ecom.Models {
    public interface ISalesChannel {

       public SalesChannelOrders.Orders Orders { get; set; }
       public SalesChannelsDiscounts.Discounts Discounts { get; set; }

    }
}
