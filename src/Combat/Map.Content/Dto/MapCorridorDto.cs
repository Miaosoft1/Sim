namespace Miaosoft.Sim.Combat.Map.Content.Dto
{
    public class MapCorridorDto
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string FromRoomId { get; set; } = string.Empty;
        public string ToRoomId { get; set; } = string.Empty;
        public double StartX { get; set; }
        public double StartY { get; set; }
        public double EndX { get; set; }
        public double EndY { get; set; }
        public double? Width { get; set; }
    }
}
