using Relatude.CMS.Models;
using Relatude.DB.Nodes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.Ecom.Models {
    public interface IShop {

        public Reference<IPage> OrderReceivedPage { get; set; }
        public Reference<IPage> ShoppingCartPage { get; set; }
        public Reference<IPage> CheckoutPage { get; set; }
        public bool PricesSpecifiedIncludeVAT { get; set; }
        ShopOrders.Orders Orders { get; set; }
        ShopTaxClasses.TaxClasses TaxClasses { get; set; }
        ShopsCurrencies.Currencies Currencies { get; set; }
        ShopsPaymentMethods.PaymentMethods PaymentMethods { get; set; }
        ShopsShippingMethods.ShippingMethods ShippingMethods { get; set; }

        DefaultTaxClassShops.TaxClass DefaultTaxClass { get; set; }
        ShippingTaxClassShops.TaxClass ShippingTaxClass { get; set; }
        DefaultCurrencyShops.Currency DefaultCurrency { get; set; }

        public Reference<IEmailContent> ConfirmationEmail { get; set; }
        public Reference<IEmailContent> CancellationEmail { get; set; }
        public Reference<IEmailContent> OrderShippedEmail { get; set; }
        public Reference<IEmailContent> RefundedEmail { get; set; }

        public Reference<ITemplate> DefaultProductTemplate { get; set; }
        public Reference<ITemplate> DefaultProductCategoryTemplate { get; set; }
    }
}
