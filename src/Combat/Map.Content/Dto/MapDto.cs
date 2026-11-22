namespace Miaosoft.Sim.Combat.Map.Content.Dto
{
    public class MapDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public double Width { get; set; }
        public double Height { get; set; }
        public string? Type { get; set; }
        public string? BoundaryType { get; set; }
        public bool? IsMainPlane { get; set; }
        public bool TradeIsolated { get; set; }
        public bool KeepSourceVisible { get; set; }
        public bool AppearanceOverlay { get; set; }
        public double Saturation { get; set; } = 1;
        public double Brightness { get; set; } = 1;
        public List<MapDungeonDto>? Dungeons { get; set; }
        public List<MapRoomDto>? Rooms { get; set; }
        public List<MapCorridorDto>? Corridors { get; set; }
    }
}
