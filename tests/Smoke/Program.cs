using HotelKeeper.Data;
using HotelKeeper.Models;
using HotelKeeper.Services;

// Smoke test of the real workflows against a live database. Usage: Smoke "<connection string>"
AppConfig.ConnectionString = args[0];
int fails = 0;
void Ok(bool c, string what) { Console.WriteLine((c ? "PASS  " : "FAIL  ") + what); if (!c) fails++; }
async Task Throws(Func<Task> f, string what) { try { await f(); Ok(false, what + " (no error)"); } catch (DomainException e) { Ok(true, what + " -> " + e.Message); } }

await DatabaseSetup.EnsureAsync();
await DatabaseSetup.EnsureAsync(); // idempotent
var admin = await AuthService.LoginAsync("admin", "Admin12345");
Ok(admin.Role == Roles.Admin && admin.MustChangePassword, "admin login, must change password");
await Throws(() => AuthService.LoginAsync("admin", "wrong"), "bad password rejected");
var rec = await AuthService.LoginAsync("reception", "Reception123");
var hk = await AuthService.LoginAsync("housekeeper", "Housekeeper123");

var guests = new GuestService(rec);
var created = await guests.RegisterAsync("Ana Cruz", "ana@example.com", "0917", "", "PH", "", "anacruz");
var gSession = await AuthService.LoginAsync("anacruz", created.TemporaryPassword);
await Throws(() => new RequestService(gSession).CreateAsync(Category.Cleaning, "clean please", "Normal"), "guest without stay cannot request");

var rooms = new RoomService(rec);
var offers = await rooms.AvailableAsync(DateTime.Today, DateTime.Today.AddDays(2), 2);
Ok(offers.Count > 0, $"{offers.Count} rooms available");
var resSvc = new ReservationService(rec);
var resId = await resSvc.CreateAsync(created.GuestId, offers[0].Id, DateTime.Today, DateTime.Today.AddDays(2), 2, "", confirm: true);
await Throws(() => resSvc.CreateAsync(created.GuestId, offers[0].Id, DateTime.Today.AddDays(1), DateTime.Today.AddDays(3), 2, ""), "overlapping booking rejected");
var stays = new StayService(rec);
var stayId = await stays.CheckInAsync(resId, offers[0].Id);
Ok((await rooms.ListAsync(search: offers[0].RoomNumber))[0].Status == RoomStatus.Occupied, "room Occupied after check-in");

var gReq = new RequestService(gSession);
var items = await gReq.ItemsAsync();
var reqId = await gReq.CreateAsync(Category.AdditionalItems, "extra towels", "Normal", null, new[] { new RequestLine(items[0].Id, 2) });
await gReq.CreateAsync(Category.Maintenance, "AC not cooling", "High", "Air conditioning");
Ok((await gReq.MineAsync()).Count == 2, "guest sees own requests");
Ok((await new RequestService(rec).ListAsync(openOnly: true)).Count >= 2, "receptionist sees requests");
await new RequestService(hk).UpdateStatusAsync(reqId, ReqStatus.InProgress, null);
await new RequestService(hk).UpdateStatusAsync(reqId, ReqStatus.Fulfilled, "delivered");
var my = await new StayService(gSession).MyActiveStayAsync();
Ok(my != null && my.RoomNumber == offers[0].RoomNumber, "guest dashboard stay");

await Throws(() => new RoomService(gSession).ListAsync(), "guest cannot list rooms");
await Throws(() => new StaffService(rec).ListAsync(), "receptionist cannot list staff");

await resSvc.AddPaymentAsync(resId, 100, "Cash", "Paid", "");
var co = await stays.CheckOutAsync(stayId);
Ok((await rooms.ListAsync(search: offers[0].RoomNumber))[0].Status == RoomStatus.Dirty, $"room Dirty after check-out (balance {co.Balance})");
var hs = new HousekeepingService(hk);
var tasks = await hs.ListAsync("open");
Ok(tasks.Count > 0, "cleaning task created");
var t = tasks.First(x => x.RoomNumber == offers[0].RoomNumber && x.TaskType != "Maintenance");
await hs.StartAsync(t.Id);
await Throws(() => hs.CompleteAsync(t.Id, null), "cannot complete with unfinished checklist");
var cl = await hs.ChecklistAsync(t.Id);
await hs.SetChecklistAsync(t.Id, cl.ToDictionary(c => c.Id, _ => true));
var outcome = await hs.CompleteAsync(t.Id, null);
Console.WriteLine("complete outcome: " + outcome);
var after = (await rooms.ListAsync(search: offers[0].RoomNumber))[0];
Ok(after.Status == RoomStatus.Available || after.OpenIssues > 0, $"room {after.Status}, open issues {after.OpenIssues} (maintenance hold respected)");

foreach (var d in new[] { await new DashboardService(admin).AdminAsync(), await new DashboardService(rec).ReceptionistAsync(), await new DashboardService(hk).HousekeeperAsync(), await new DashboardService(gSession).GuestAsync(), await new DashboardService(admin).AnalyticsAsync() })
    Ok(d.Cards.Count > 0 || d.Panels.Count > 0, "dashboard loads");
Ok((await new AuditService(admin).ListAsync()).Count > 5, "audit log populated");
Console.WriteLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
return fails;
