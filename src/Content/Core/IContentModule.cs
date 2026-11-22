namespace Miaosoft.Sim.Content.Core;

public interface IContentModule
{
    string Id { get; }
    void Register(ContentSectionRegistry registry);
}
