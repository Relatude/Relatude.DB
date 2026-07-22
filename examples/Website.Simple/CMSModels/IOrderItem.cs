using Relatude.CMS.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.Ecom.Models {
    public interface IOrderItem:IItem {
        OrderItems.Order Order { get; set; }

        ProductOrderItems.Product Product { get; set; }
        SimpleVariantsOrderItems.SimpleVariant SimpleVariant { get; set; }
        decimal ItemTotalExVat { get; set; }
        decimal ItemTotalIncVat {  get; set; }
        int Quantity { get; set; }

        decimal UnitPriceExVat { get; set; }
        decimal UnitPriceIncVat { get; set; }   
        decimal DiscountAmountExVat { get; set; }
        decimal DiscountAmountIncVat { get; set; }
        string ProductName{get;set;}
        Guid ProductId { get; set; }    
        int ShippedQuantity { get; set; }   
        Guid OrderId{get;set;}


    }
}
