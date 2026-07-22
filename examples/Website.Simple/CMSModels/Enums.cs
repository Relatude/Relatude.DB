using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.Ecom.Models {

    public enum OrderStatus : int {
        Basket = 0,
        Ordered = 1,
        Refunded = 2,
        Cancelled = 3,
        Unconfirmed = 4,
        Saved = 5,
        Shipped = 6,
        PartlyShipped = 7,
    }

    public enum PaymentStatus : int {
        NotStarted = 0,
        Refunded = 1,
        Reserved = 2,
        Started = 3,
        Completed = 4,
        Declined = 5,
        Failed = 6,
        Cancelled = 7,
        ReservedPartlyCaptured = 8,
        AwaitingApproval = 9,
    }

    public enum TypeOfPayment : int {
        OnePhase = 0,
        TwoPhase = 1,
    }

}
