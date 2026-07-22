using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.CMS.Models {
    public interface IEmailContent {

        public string FromEmail { get; set; }
        public string FromEmailName { get; set; } 
        public string CCEmails { get; set; } 
        public string Subject { get; set; } 
        public string Body { get; set; } 

        public string ReplyToEmail { get; set; } 


    }
}
