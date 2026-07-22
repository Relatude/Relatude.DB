using Relatude.CMS.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.Ecom.Models {
    public interface IOrder : IItem{

        public OrderStatus OrderStatus { get; set; }   
        public CurrencyOrders.Currency Currency { get; set; }
        public EcomUserOrders.Customer Customer { get; set; }
        public ShopOrders.Shop Shop { get; set; }
        public OrderItems.Items OrderItems { get; set; }
        public SalesChannelOrders.SalesChannel SalesChannel { get; set; }
        public PaymentMethodOrders.PaymentMethod PaymentMethod { get; set; }
         public string PaymentProviderKey { get; set; } 
        decimal OrderTotal { get; set; }
        decimal OrderTaxAmount { get; set; }
       
        decimal ItemsSubtotalNet { get; set; }
        decimal ItemsSubtotalGross { get; set; }
        decimal ItemsTaxAmount {  get; set; }
        decimal ShippingAmountNet {  get; set; }
        decimal ShippingTaxAmount { get; set; }
        decimal ShippingAmountGross { get; set; }

        decimal DiscountAmountNet {  get; set; }
        decimal DiscountTaxAmount { get; set; }
        decimal DiscountAmountGross { get; set; }

        decimal CreditedAmount { get; set; }
        decimal RefundedAmount { get; set; }
        decimal CapturedAmount { get; set; }
        decimal RemainingAmountToCapture { get; set; }  
        DateTime DateOrdered {  get; set; }
        DateTime DateShipped { get; set; }

        
        public string Email{ get; set; }
        public string Mobile{ get; set; }

        public string BillingCompany{ get; set; }
        public string BillingForename{ get; set; }
        public string BillingSurname{ get; set; }
        public string BillingStreetAddress{ get; set; }
        public string BillingCity { get; set; }
        public string BillingZipCode{ get; set; }
        public string BillingCountry { get; set; }

        public string ShippingCompany{ get; set; }
        public string ShippingForename{ get;set; }
        public string ShippingSurname{ get; set; }
        public string ShippingStreetAddress{ get; set; }
        public string ShippingCity { get; set; }
        public string ShippingZipCode{ get; set; }
        public string ShippingCountry { get; set; }
        public string ShippingNotes{ get; set; }
        public string ShippingMobile { get; set; }
        public string OrderReference { get; set; }
        public string CouponCodes{ get; set; }
        public string PaymentTransactionId { get; set; }
        public bool UsesSameBillingAndShippingAddress { get; set; }
        public string TrackingNumber { get; set; }
        public string TrackingUrl { get; set; }
        public string LabelUrl { get; set; }
        public bool ShippingAmountCaptured { get; set; }

    }
}
