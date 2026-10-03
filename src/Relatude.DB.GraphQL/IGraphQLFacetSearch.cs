using Microsoft.AspNetCore.Http;
using Relatude.DB.Datamodels;

namespace Relatude.DB.GraphQL;

/// <summary>
/// Answers the facet search an endpoint serves beside its schema (<see cref="GraphQLEndpointDefinition.EnableFacetSearch"/>):
/// the requests carrying a "facets" parameter, which reach it after the API key has been checked. What it may search is
/// the endpoint's <see cref="RelatudeGraphQL.FacetScope"/>, and it reads in the query context the endpoint's queries run in.
/// The library defines the requests and the page that makes them; the search itself needs the query engine's typed API,
/// which the Relatude.DB server has (NodeServer/GraphQL/GraphQLFacetSearch.cs).
/// </summary>
public interface IGraphQLFacetSearch {
    /// <summary>Answers one request; <paramref name="action"/> is the value of the "facets" parameter.</summary>
    Task HandleAsync(HttpContext http, RelatudeGraphQL endpoint, string action, QueryContext? queryContext);
}
