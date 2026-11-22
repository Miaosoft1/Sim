namespace Miaosoft.Sim.Combat.Map.Content.Dto
{
    public class MapRoomDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Type { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool IsEntrance { get; set; }
        public bool IsExit { get; set; }
        public string? Description { get; set; }
    }
}
