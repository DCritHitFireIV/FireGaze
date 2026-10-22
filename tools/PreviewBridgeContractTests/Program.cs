using System.Text.Json;
using FireGaze.LocalPreview.Runtime;
using HMSync.Services;

var baseline = new PresentationSnapshot(1, 2, 3, 4, 5, 6, 7, true,
    8, 9, 10.5f, 11, 12, 13, 14.5f, 15.5f, 16.5f, 17.5f,
    18, 19, 20.5f, 21.5f, 22.5f);
var sent = new CaptureProjection(1, true, 1234, 567, 89, baseline);
var received = LocalPreviewProjection.Read(JsonSerializer.Serialize(sent), 1234, 567);
if (!received.ShouldCapture || received.Baseline is null || received.Revision != 89)
    throw new Exception("Active preview projection failed to roundtrip.");
foreach (var source in typeof(PresentationSnapshot).GetProperties())
{
    var target = typeof(LocalPreviewBaseline).GetProperty(source.Name)
        ?? throw new Exception($"HMS schema missing {source.Name}.");
    if (!Equals(source.GetValue(baseline), target.GetValue(received.Baseline)))
        throw new Exception($"HMS received a different {source.Name} value.");
}
Console.WriteLine("PASS: real FireGaze DTO roundtrips every field to HMS receiver");
var inactive = LocalPreviewProjection.Read(
    JsonSerializer.Serialize(sent with { Active = false, Baseline = null }), 1234, 567);
if (!inactive.ShouldCapture || inactive.Baseline is not null)
    throw new Exception("Inactive preview must permit ordinary capture.");
Console.WriteLine("PASS: inactive preview permits ordinary capture");
Console.WriteLine("2/2 bridge contract tests passed.");
