namespace Website.Simple.Models;

// The datamodel for this type is defined in Models/Json/testimonial.json (a JsonFile datamodel
// source, see relatude.db.json) - that is why the class carries no Relatude attributes. The class
// only has to match the JSON model by full name and property names, so model settings (ids,
// indexing, defaults) can be changed without touching code. The typed API works as usual, see the
// /testimonials endpoint in Program.cs.
public class Testimonial {
    public Guid Id { get; set; }
    public string Author { get; set; } = "";
    public string Quote { get; set; } = "";
    public int Rating { get; set; }
}
