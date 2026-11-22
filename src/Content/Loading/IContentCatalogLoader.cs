namespace Miaosoft.Sim.Content.Loading;

public interface IContentCatalogLoader
{
    ContentCatalog Load(IEnumerable<ContentDocument> documents, IEnumerable<string> moduleIds);
}
