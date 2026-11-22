namespace Miaosoft.Sim.Combat.Map.Content.Dto
{
    public class MapDungeonDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public double? EntranceX { get; set; }
        public double? EntranceY { get; set; }
        public double? InteractionRadius { get; set; }
        public string? TargetMapId { get; set; }
    }
}
