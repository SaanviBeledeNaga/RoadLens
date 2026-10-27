using VehicleCounter.Api.Models;
using VehicleCounter.Api.Services;
static Detection At(double x, double bottom) => new("car", .9f, new Box(x - 10, bottom - 20, 20, 20));
static VehicleTracker Counter() => new(new CountingLine(new(100, 200), new(300, 200)), 400, 400);
static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS: " + name); }
var counter = Counter();
counter.Update([At(150,190)],0);
counter.Update([At(150,200)],1);
counter.Update([At(150,210)],2);
Check(counter.Counts["car"] == 1, "Counts crossing through the tolerance band");
Check(counter.Directions["A to B"] == 1, "Records crossing direction");
counter.Update([At(150,200)],3);
counter.Update([At(150,190)],4);
Check(counter.Counts["car"] == 1, "Does not count the same track twice after reversal");
var outside = Counter(); outside.Update([At(350,190)],0); outside.Update([At(350,210)],1);
Check(outside.Counts["car"] == 0, "Rejects crossing outside the finite line segment");
var jitter = Counter(); jitter.Update([At(150,199)],0); jitter.Update([At(150,201)],1); jitter.Update([At(150,199)],2);
Check(jitter.Counts["car"] == 0, "Ignores jitter near the line");
var two = Counter(); two.Update([At(150,190),At(250,190)],0); two.Update([At(150,210),At(250,210)],1);
Check(two.Counts["car"] == 2, "Counts two separate identities");
var reverse = Counter(); reverse.Update([At(150,210)],0); reverse.Update([At(150,190)],1);
Check(reverse.Directions["B to A"] == 1, "Counts reverse direction");
Check(Counter().Counts["car"] == 0, "Keeps separate job counts isolated");
// Regression: detections captured from the two-car sample around both crossings.
using var fixture=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures/two-car-detections.json")));
int Replay(float threshold)
{
    var tracker=new VehicleTracker(new CountingLine(new(0.02*768,0.6*432),new(0.98*768,0.6*432)),768,432);
    var previous=-2;
    foreach(var row in fixture.RootElement.EnumerateArray())
    {
        var frame=row.GetProperty("frame").GetInt32();
        while(previous+2<frame){previous+=2;tracker.Update([],previous/12d);}
        var detections=System.Text.Json.JsonSerializer.Deserialize<Detection[]>(row.GetProperty("detections").GetRawText(),new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        tracker.Update(detections.Where(d=>d.Confidence>=threshold).ToArray(),frame/12d);
        previous=frame;
    }
    return tracker.Counts.Values.Sum();
}
Check(Replay(.35f)==1,"Reproduces the old threshold missing the first car");
Check(Replay(.25f)==2,"25% sample setting retains both crossings");

var activity = Counter();
activity.Update([At(150,190),At(250,170)],1);
Check(activity.Vehicles.Count==2 && activity.Vehicles.All(v=>!v.Crossed),"Lists every detected track before any crossing");
activity.Update([At(150,210),At(250,175)],2);
Check(activity.Vehicles.Count==2 && activity.Vehicles.Count(v=>v.Crossed)==1,"Updates the existing activity row when a track crosses");
Check(activity.Vehicles.Single(v=>v.Crossed).CrossedAtSeconds==2,"Records crossing time in the vehicle activity");
Check(activity.Vehicles.All(v=>v.FirstSeenSeconds==1 && v.LastSeenSeconds==2),"Tracks first and last detection times");
for(var frame=0;frame<14;frame++)activity.Update([],3+frame);
Check(activity.Vehicles.Count==2,"Retains activity after tracks leave the frame");
Check(activity.Vehicles.Count(v=>v.Crossed)==activity.Events.Count,"Activity crossing status agrees with the crossing event log");
var reviewJob=new JobState(Guid.NewGuid(),"fixture.mp4","fixture.mp4",new AnalysisSettings(new CountingLine(new(0,0),new(1,0))));
reviewJob.RecordTracks([new TrackView(4,"car",.8f,new Box(10,20,30,40),false)],12,.4);
reviewJob.RecordTracks([new TrackView(4,"car",.9f,new Box(12,22,30,40),true)],14,14/30d);
Check(reviewJob.GetObservations(4).Count==2 && reviewJob.GetObservations(4)[0].SourceFrame==12,"Stores exact-frame vehicle positions for review");
Check(reviewJob.GetObservations(999).Count==0,"Unknown tracks have no invented review positions");
var restoredJob=new JobState(Guid.NewGuid(),"fixture.mp4","fixture.mp4",reviewJob.Settings);
restoredJob.RestoreObservations(reviewJob.GetObservationIndex());
Check(restoredJob.GetObservations(4).SequenceEqual(reviewJob.GetObservations(4)),"Vehicle review positions survive persistence round trip");
var duplicate=Counter();
duplicate.Update([At(150,190),new Detection("truck",.6f,new Box(140,170,20,20))],0);
duplicate.Update([At(150,210),new Detection("truck",.5f,new Box(140,190,20,20))],.1);
Check(duplicate.Vehicles.Count==1 && duplicate.Events.Count==1,"Overlapping car/truck predictions create one identity and crossing");
var dropout=Counter();
dropout.Update([At(150,170)],0);
for(var tick=1;tick<=18;tick++)dropout.Update([],tick/15d);
var recovered=dropout.Update([At(152,172)],1.3);
Check(dropout.Vehicles.Count==1 && recovered.Single().Id==1,"Recovers the same identity after a short detection dropout");
dropout.Update([],4);
dropout.Update([At(152,172)],4.1);
Check(dropout.Vehicles.Count==2,"Does not merge a new arrival after an extended absence");
var assignment=Counter();
assignment.Update([new Detection("car",.9f,new Box(70,170,60,20)),new Detection("car",.8f,new Box(110,170,60,20))],0);
var assigned=assignment.Update([new Detection("car",.9f,new Box(85,170,60,20)),new Detection("car",.8f,new Box(50,170,60,20))],.1);
Check(assignment.Vehicles.Count==2 && assigned.Single(t=>t.Id==1).Box.Center.X==80 && assigned.Single(t=>t.Id==2).Box.Center.X==115,"Global assignment avoids creating a repeated identity when two matches compete");
