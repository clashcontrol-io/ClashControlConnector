// WebSocketServer.cs reports App.Version on its liveness probe; the real App
// class pulls in the whole Revit API, so tests use this stand-in.
namespace ClashControlConnector
{
    public static class App
    {
        public const string Version = "test";
    }
}
