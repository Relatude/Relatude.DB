export type CaseChange = "NoTransform" | "ToLowerCase" | "ToUpperCase";

export interface PatternVariant {
  label: string;
  pattern: string;
  matches: string[];
  doesNotMatch: string[];
  message?: string;
  caseChange?: CaseChange;
}

export interface PatternTemplate {
  id: string;
  group: string;
  name: string;
  description: string;
  message: string;
  trim?: boolean;
  caseChange?: CaseChange;
  variantLabel?: string;
  variants: PatternVariant[];
}

const r = String.raw;

export const patternGroups = ["Contact", "Web and network", "Places", "Codes and numbers", "Dates and times", "Text"];

export const patternTemplates: PatternTemplate[] = [
  {
    id: "email",
    group: "Contact",
    name: "Email address",
    description: "Something before an @, a domain after it, and a dot in the domain. Catches typing mistakes without refusing real but unusual addresses.",
    message: "Enter a valid email address, like name@example.com",
    trim: true,
    caseChange: "ToLowerCase",
    variants: [
      { label: "Common addresses", pattern: r`^[^\s@]+@[^\s@]+\.[^\s@]{2,}$`, matches: ["ole@example.com", "first.last+news@mail.example.no"], doesNotMatch: ["ole@example", "ole example.com", "@example.com"] },
      {
        label: "Stricter (letters, digits and . _ % + - only)",
        pattern: r`^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$`,
        matches: ["ole@example.com", "kari_nordmann@firma.no"],
        doesNotMatch: ["ole@exämple.com", "ole@@example.com", "ole@example.c"],
      },
    ],
  },
  {
    id: "phone",
    group: "Contact",
    name: "Phone or mobile number",
    description: "Phone numbers differ from country to country. Pick the country, then adjust if needed. Spaces are not allowed in the strict formats; turn on Trim and ask people to type the number without spaces, or use the lenient format.",
    message: "Enter a valid phone number",
    trim: true,
    variantLabel: "Country or format",
    variants: [
      { label: "International (+ and country code)", pattern: r`^\+[1-9]\d{6,14}$`, matches: ["+4791234567", "+14155552671"], doesNotMatch: ["91234567", "+0123456", "+47 912 34 567"], message: "Enter the number with + and the country code, like +4791234567" },
      { label: "Lenient (digits, spaces, dashes, brackets)", pattern: r`^\+?[0-9 ()\-]{6,20}$`, matches: ["+47 912 34 567", "(415) 555-2671", "22 33 44 55"], doesNotMatch: ["call me", "12", "+47 912 34 567 ext 2"] },
      { label: "Norway, mobile", pattern: r`^(\+47)?[49]\d{7}$`, matches: ["91234567", "+4741234567"], doesNotMatch: ["21234567", "9123456", "+4691234567"] },
      { label: "Norway, any number", pattern: r`^(\+47)?[2-9]\d{7}$`, matches: ["22334455", "+4791234567"], doesNotMatch: ["12345678", "2233445"] },
      { label: "Sweden, mobile", pattern: r`^(\+46|0)7\d{8}$`, matches: ["0701234567", "+46701234567"], doesNotMatch: ["070123456", "+4670123456"] },
      { label: "Denmark", pattern: r`^(\+45)?\d{8}$`, matches: ["20123456", "+4520123456"], doesNotMatch: ["2012345", "+4620123456"] },
      { label: "Finland, mobile", pattern: r`^(\+358|0)4\d{7,8}$`, matches: ["0401234567", "+358401234567"], doesNotMatch: ["0201234567", "+35840123"] },
      { label: "Germany, mobile", pattern: r`^(\+49|0)1[5-7]\d{8,9}$`, matches: ["015112345678", "+4917612345678"], doesNotMatch: ["03012345678", "+491512345"] },
      { label: "United Kingdom, mobile", pattern: r`^(\+44|0)7\d{9}$`, matches: ["07700900123", "+447700900123"], doesNotMatch: ["02079460000", "0770090012"] },
      { label: "United States and Canada", pattern: r`^(\+1)?[2-9]\d{2}[2-9]\d{6}$`, matches: ["4155552671", "+14155552671"], doesNotMatch: ["1155552671", "415555267"] },
      { label: "Australia, mobile", pattern: r`^(\+61|0)4\d{8}$`, matches: ["0412345678", "+61412345678"], doesNotMatch: ["0212345678", "041234567"] },
    ],
  },
  {
    id: "person-name",
    group: "Contact",
    name: "Person name",
    description: "Letters from any language, with spaces, hyphens and apostrophes between them - so names like Øyvind, José, O'Brien and Anne-Marie are all accepted, digits and symbols are not.",
    message: "Use letters only, with spaces, hyphens or apostrophes between names",
    trim: true,
    variants: [{ label: "Any language", pattern: r`^\p{L}+([ '\-]\p{L}+)*$`, matches: ["Øyvind", "Anne-Marie", "O'Brien", "José Luis"], doesNotMatch: ["Ole2", "Ole  Hansen", "-Ole"] }],
  },
  {
    id: "url",
    group: "Web and network",
    name: "Web address (URL)",
    description: "An address starting with http:// or https://, with no spaces in it.",
    message: "Enter a full web address starting with https://",
    trim: true,
    variants: [
      { label: "http or https", pattern: r`^https?://[^\s/$.?#][^\s]*$`, matches: ["https://relatude.com", "http://example.com/path?q=1"], doesNotMatch: ["relatude.com", "https://", "ftp://example.com"] },
      { label: "https only", pattern: r`^https://[^\s/$.?#][^\s]*$`, matches: ["https://relatude.com/docs"], doesNotMatch: ["http://relatude.com", "relatude.com"] },
    ],
  },
  {
    id: "domain",
    group: "Web and network",
    name: "Domain name",
    description: "A host name such as example.com or shop.example.no, without http:// and without a path.",
    message: "Enter a domain name, like example.com",
    trim: true,
    caseChange: "ToLowerCase",
    variants: [
      {
        label: "Domain",
        pattern: r`^([a-z0-9]([a-z0-9\-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$`,
        matches: ["example.com", "shop.example.no"],
        doesNotMatch: ["https://example.com", "example", "-example.com"],
      },
    ],
  },
  {
    id: "slug",
    group: "Web and network",
    name: "URL slug",
    description: "The readable part of an address, like summer-sale-2026: lower case letters and digits, with single dashes between words.",
    message: "Use lower case letters and digits, with dashes between words",
    trim: true,
    caseChange: "ToLowerCase",
    variants: [{ label: "Lower case words with dashes", pattern: r`^[a-z0-9]+(-[a-z0-9]+)*$`, matches: ["summer-sale-2026", "about"], doesNotMatch: ["Summer Sale", "summer--sale", "-about"] }],
  },
  {
    id: "ipv4",
    group: "Web and network",
    name: "IP address (IPv4)",
    description: "Four numbers from 0 to 255 separated by dots.",
    message: "Enter an IP address, like 192.168.1.10",
    trim: true,
    variants: [
      {
        label: "IPv4",
        pattern: r`^((25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)$`,
        matches: ["192.168.1.10", "8.8.8.8"],
        doesNotMatch: ["256.1.1.1", "192.168.1", "192.168.01.1"],
      },
    ],
  },
  {
    id: "hex-colour",
    group: "Web and network",
    name: "Colour code",
    description: "A web colour written as # and 3 or 6 hexadecimal digits.",
    message: "Enter a colour like #1a7f37",
    trim: true,
    caseChange: "ToLowerCase",
    variants: [{ label: "#rgb or #rrggbb", pattern: r`^#([0-9a-f]{3}|[0-9a-f]{6})$`, matches: ["#1a7f37", "#fff"], doesNotMatch: ["1a7f37", "#12345", "#ggg"] }],
  },
  {
    id: "postcode",
    group: "Places",
    name: "Post code",
    description: "Post codes look different in every country. Pick the country, and adjust the pattern if you need to accept more than one country or format.",
    message: "Enter a valid post code",
    trim: true,
    variantLabel: "Country",
    variants: [
      { label: "Norway", pattern: r`^\d{4}$`, matches: ["0150", "7030"], doesNotMatch: ["150", "N-0150"], message: "Enter a post code of four digits, like 0150" },
      { label: "Sweden", pattern: r`^\d{3} ?\d{2}$`, matches: ["114 55", "11455"], doesNotMatch: ["1145", "114-55"], message: "Enter a post code like 114 55" },
      { label: "Denmark", pattern: r`^\d{4}$`, matches: ["1050", "8000"], doesNotMatch: ["DK-1050", "105"], message: "Enter a post code of four digits, like 1050" },
      { label: "Finland", pattern: r`^\d{5}$`, matches: ["00100"], doesNotMatch: ["0100", "FI-00100"], message: "Enter a post code of five digits, like 00100" },
      { label: "Germany", pattern: r`^\d{5}$`, matches: ["10115"], doesNotMatch: ["1011", "D-10115"], message: "Enter a post code of five digits, like 10115" },
      { label: "France", pattern: r`^\d{5}$`, matches: ["75001"], doesNotMatch: ["7500"], message: "Enter a post code of five digits, like 75001" },
      { label: "Netherlands", pattern: r`^\d{4} ?[A-Z]{2}$`, matches: ["1012 AB", "1012AB"], doesNotMatch: ["1012", "AB 1012"], message: "Enter a post code like 1012 AB", caseChange: "ToUpperCase" },
      {
        label: "United Kingdom",
        pattern: r`^[A-Z]{1,2}\d[A-Z\d]? ?\d[A-Z]{2}$`,
        matches: ["SW1A 1AA", "M1 1AE", "EC1A1BB"],
        doesNotMatch: ["SW1A", "12345"],
        message: "Enter a postcode like SW1A 1AA",
        caseChange: "ToUpperCase",
      },
      { label: "United States (ZIP)", pattern: r`^\d{5}(-\d{4})?$`, matches: ["94105", "94105-1234"], doesNotMatch: ["9410", "94105-12"], message: "Enter a ZIP code like 94105 or 94105-1234" },
      { label: "Canada", pattern: r`^[A-Z]\d[A-Z] ?\d[A-Z]\d$`, matches: ["K1A 0B1", "K1A0B1"], doesNotMatch: ["K1A 0B", "12345"], message: "Enter a postal code like K1A 0B1", caseChange: "ToUpperCase" },
      { label: "Poland", pattern: r`^\d{2}-\d{3}$`, matches: ["00-950"], doesNotMatch: ["00950"], message: "Enter a post code like 00-950" },
      { label: "Spain and Italy", pattern: r`^\d{5}$`, matches: ["28013", "00184"], doesNotMatch: ["2801"], message: "Enter a post code of five digits" },
      { label: "Australia", pattern: r`^\d{4}$`, matches: ["2000"], doesNotMatch: ["200"], message: "Enter a postcode of four digits, like 2000" },
      { label: "Japan", pattern: r`^\d{3}-?\d{4}$`, matches: ["100-0001", "1000001"], doesNotMatch: ["100-001"], message: "Enter a post code like 100-0001" },
      { label: "Brazil", pattern: r`^\d{5}-?\d{3}$`, matches: ["01310-100"], doesNotMatch: ["0131-100"], message: "Enter a CEP like 01310-100" },
      { label: "India", pattern: r`^[1-9]\d{5}$`, matches: ["110001"], doesNotMatch: ["010001", "11000"], message: "Enter a PIN code of six digits" },
      { label: "Any country (letters, digits, space, dash)", pattern: r`^[A-Z0-9][A-Z0-9 \-]{1,8}[A-Z0-9]$`, matches: ["0150", "SW1A 1AA", "00-950"], doesNotMatch: ["#150", "A"], caseChange: "ToUpperCase" },
    ],
  },
  {
    id: "country",
    group: "Places",
    name: "Country code",
    description: "Two letter ISO 3166 country code, like NO, SE or GB.",
    message: "Enter a two letter country code, like NO",
    trim: true,
    caseChange: "ToUpperCase",
    variants: [{ label: "ISO 3166-1 alpha-2", pattern: r`^[A-Z]{2}$`, matches: ["NO", "GB"], doesNotMatch: ["NOR", "N"] }],
  },
  {
    id: "product-code",
    group: "Codes and numbers",
    name: "Product code (SKU)",
    description: "Every shop has its own product codes, so start from the shape closest to yours and change the letters and numbers in the pattern to fit.",
    message: "Enter a product code like ABC-1234",
    trim: true,
    caseChange: "ToUpperCase",
    variantLabel: "Shape",
    variants: [
      { label: "Three letters, dash, four digits (ABC-1234)", pattern: r`^[A-Z]{3}-\d{4}$`, matches: ["ABC-1234"], doesNotMatch: ["AB-1234", "ABC1234", "ABC-123"] },
      { label: "Letters and digits with dashes", pattern: r`^[A-Z0-9]+(-[A-Z0-9]+)*$`, matches: ["TSHIRT-RED-XL", "A1000"], doesNotMatch: ["TSHIRT--XL", "-A1000", "A 1000"], message: "Use letters and digits, with single dashes between parts" },
      { label: "Fixed length, 8 letters or digits", pattern: r`^[A-Z0-9]{8}$`, matches: ["AB12CD34"], doesNotMatch: ["AB12CD3", "AB12-CD34"], message: "Enter eight letters or digits" },
      { label: "EAN-13 barcode", pattern: r`^\d{13}$`, matches: ["7038010009457"], doesNotMatch: ["703801000945", "70380100094570"], message: "Enter the 13 digits of the barcode" },
      { label: "UPC-A barcode", pattern: r`^\d{12}$`, matches: ["036000291452"], doesNotMatch: ["03600029145"], message: "Enter the 12 digits of the barcode" },
      { label: "ISBN-13", pattern: r`^97[89]\d{10}$`, matches: ["9788202512132"], doesNotMatch: ["9688202512132", "978820251213"], message: "Enter an ISBN of 13 digits starting with 978 or 979" },
    ],
  },
  {
    id: "number",
    group: "Codes and numbers",
    name: "Number written as text",
    description: "For numbers kept in a text property, such as order numbers or customer numbers.",
    message: "Enter a number",
    trim: true,
    variantLabel: "Kind",
    variants: [
      { label: "Digits only", pattern: r`^\d+$`, matches: ["000123", "42"], doesNotMatch: ["-42", "4.2", "12a"], message: "Use digits only" },
      { label: "Fixed number of digits (6)", pattern: r`^\d{6}$`, matches: ["100234"], doesNotMatch: ["10023", "1002345"], message: "Enter six digits" },
      { label: "Whole number, may be negative", pattern: r`^-?\d+$`, matches: ["-42", "7"], doesNotMatch: ["4.2", "--4"] },
      { label: "Decimal number (point or comma)", pattern: r`^-?\d+([.,]\d+)?$`, matches: ["3.14", "-0,5", "12"], doesNotMatch: ["3.", ".5", "1.2.3"] },
    ],
  },
  {
    id: "iban",
    group: "Codes and numbers",
    name: "Bank account (IBAN)",
    description: "Two letters for the country, two check digits and up to 30 letters or digits, written without spaces. The pattern checks the shape, not the check digits.",
    message: "Enter the IBAN without spaces, like NO9386011117947",
    trim: true,
    caseChange: "ToUpperCase",
    variants: [{ label: "IBAN without spaces", pattern: r`^[A-Z]{2}\d{2}[A-Z0-9]{11,30}$`, matches: ["NO9386011117947", "GB29NWBK60161331926819"], doesNotMatch: ["NO93 8601 1117 947", "9386011117947"] }],
  },
  {
    id: "org-number",
    group: "Codes and numbers",
    name: "Organisation or VAT number",
    description: "Company registration numbers. The pattern checks the shape only.",
    message: "Enter a valid organisation number",
    trim: true,
    variantLabel: "Country",
    variants: [
      { label: "Norway, organisation number", pattern: r`^\d{9}$`, matches: ["923609016"], doesNotMatch: ["92360901", "923 609 016"], message: "Enter the nine digits of the organisation number" },
      { label: "Sweden, organisation number", pattern: r`^\d{6}-\d{4}$`, matches: ["556036-0793"], doesNotMatch: ["5560360793"], message: "Enter the organisation number like 556036-0793" },
      { label: "EU VAT number", pattern: r`^[A-Z]{2}[A-Z0-9]{2,12}$`, matches: ["SE556036079301", "DE123456789"], doesNotMatch: ["123456789", "DE 123456789"], message: "Enter the VAT number with the country prefix, like DE123456789", caseChange: "ToUpperCase" },
    ],
  },
  {
    id: "currency",
    group: "Codes and numbers",
    name: "Currency code",
    description: "Three letter ISO 4217 currency code, like NOK, EUR or USD.",
    message: "Enter a three letter currency code, like NOK",
    trim: true,
    caseChange: "ToUpperCase",
    variants: [{ label: "ISO 4217", pattern: r`^[A-Z]{3}$`, matches: ["NOK", "EUR"], doesNotMatch: ["NO", "EURO"] }],
  },
  {
    id: "culture",
    group: "Codes and numbers",
    name: "Language code",
    description: "A language, optionally with a country, like en, nb-NO or en-GB.",
    message: "Enter a language code like en or nb-NO",
    trim: true,
    variants: [{ label: "language or language-COUNTRY", pattern: r`^[a-z]{2,3}(-[A-Z]{2})?$`, matches: ["en", "nb-NO"], doesNotMatch: ["EN", "nb_NO", "english"] }],
  },
  {
    id: "guid",
    group: "Codes and numbers",
    name: "Guid",
    description: "A globally unique id in its usual form of 32 hexadecimal digits in groups of 8-4-4-4-12.",
    message: "Enter a guid like 3f2504e0-4f89-11d3-9a0c-0305e82c3301",
    trim: true,
    caseChange: "ToLowerCase",
    variants: [
      {
        label: "8-4-4-4-12",
        pattern: r`^[0-9a-f]{8}-([0-9a-f]{4}-){3}[0-9a-f]{12}$`,
        matches: ["3f2504e0-4f89-11d3-9a0c-0305e82c3301"],
        doesNotMatch: ["3f2504e04f8911d39a0c0305e82c3301", "{3f2504e0-4f89-11d3-9a0c-0305e82c3301}"],
      },
    ],
  },
  {
    id: "version",
    group: "Codes and numbers",
    name: "Version number",
    description: "Three numbers separated by dots, optionally followed by a dash and a label: 1.4.2 or 2.0.0-beta.1.",
    message: "Enter a version like 1.4.2",
    trim: true,
    variants: [{ label: "major.minor.patch", pattern: r`^\d+\.\d+\.\d+(-[0-9A-Za-z.\-]+)?$`, matches: ["1.4.2", "2.0.0-beta.1"], doesNotMatch: ["1.4", "v1.4.2"] }],
  },
  {
    id: "date",
    group: "Dates and times",
    name: "Date written as text",
    description: "For dates kept in a text property. A date property is usually the better choice; use this when the text form itself matters. The pattern checks the shape, so 31.02 passes.",
    message: "Enter a date",
    trim: true,
    variantLabel: "Format",
    variants: [
      { label: "Year-month-day (2026-10-17)", pattern: r`^\d{4}-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])$`, matches: ["2026-10-17"], doesNotMatch: ["2026-13-01", "17.10.2026", "2026-1-7"], message: "Enter a date like 2026-10-17" },
      { label: "Day.month.year (17.10.2026)", pattern: r`^(0[1-9]|[12]\d|3[01])\.(0[1-9]|1[0-2])\.\d{4}$`, matches: ["17.10.2026"], doesNotMatch: ["2026-10-17", "32.10.2026"], message: "Enter a date like 17.10.2026" },
      { label: "Day/month/year (17/10/2026)", pattern: r`^(0[1-9]|[12]\d|3[01])/(0[1-9]|1[0-2])/\d{4}$`, matches: ["17/10/2026"], doesNotMatch: ["10/32/2026"], message: "Enter a date like 17/10/2026" },
      { label: "Month/day/year (10/17/2026)", pattern: r`^(0[1-9]|1[0-2])/(0[1-9]|[12]\d|3[01])/\d{4}$`, matches: ["10/17/2026"], doesNotMatch: ["17/10/2026"], message: "Enter a date like 10/17/2026" },
      { label: "Year (1900-2099)", pattern: r`^(19|20)\d{2}$`, matches: ["2026", "1999"], doesNotMatch: ["26", "2126"], message: "Enter a four digit year" },
    ],
  },
  {
    id: "time",
    group: "Dates and times",
    name: "Time of day",
    description: "Hours and minutes on a 24 hour clock.",
    message: "Enter a time like 14:30",
    trim: true,
    variantLabel: "Format",
    variants: [
      { label: "HH:MM", pattern: r`^([01]\d|2[0-3]):[0-5]\d$`, matches: ["14:30", "00:05"], doesNotMatch: ["24:00", "9:30", "14.30"] },
      { label: "HH:MM:SS", pattern: r`^([01]\d|2[0-3]):[0-5]\d:[0-5]\d$`, matches: ["14:30:00"], doesNotMatch: ["14:30"], message: "Enter a time like 14:30:00" },
    ],
  },
  {
    id: "letters",
    group: "Text",
    name: "Letters only",
    description: "Letters from any language and nothing else - no digits, spaces or symbols.",
    message: "Use letters only",
    trim: true,
    variants: [
      { label: "Letters, any language", pattern: r`^\p{L}+$`, matches: ["Bergen", "Tromsø"], doesNotMatch: ["Bergen 2", "Oslo-Vest"] },
      { label: "Letters and digits", pattern: r`^[\p{L}\p{Nd}]+$`, matches: ["Room42", "Ålesund1"], doesNotMatch: ["Room 42", "Room-42"], message: "Use letters and digits only" },
      { label: "Letters, digits and spaces", pattern: r`^[\p{L}\p{Nd} ]+$`, matches: ["Room 42"], doesNotMatch: ["Room-42", "Room_42"], message: "Use letters, digits and spaces only" },
    ],
  },
  {
    id: "username",
    group: "Text",
    name: "User name",
    description: "3 to 20 characters: letters a-z, digits, dots and underscores.",
    message: "Use 3 to 20 letters, digits, dots or underscores",
    trim: true,
    caseChange: "ToLowerCase",
    variants: [{ label: "3-20 characters", pattern: r`^[a-z0-9._]{3,20}$`, matches: ["ole.g", "kari_99"], doesNotMatch: ["ol", "ole gulbrandsen", "ole@home"] }],
  },
  {
    id: "single-line",
    group: "Text",
    name: "One line, no extra spaces",
    description: "Text without line breaks, and not starting or ending with a space.",
    message: "Write it on one line, without spaces at the start or end",
    variants: [
      { label: "One line, no spaces at the ends", pattern: r`^\S([^\r\n]*\S)?$`, matches: ["A title", "X"], doesNotMatch: [" A title", "A title ", "Two\nlines"] },
      { label: "One line", pattern: r`^[^\r\n]*$`, matches: ["A title", " spaced "], doesNotMatch: ["Two\nlines"], message: "Write it on one line" },
    ],
  },
];

export function findTemplateFor(pattern: string): { template: PatternTemplate; variant: PatternVariant } | null {
  for (const template of patternTemplates) {
    for (const variant of template.variants) {
      if (variant.pattern === pattern) return { template, variant };
    }
  }
  return null;
}
