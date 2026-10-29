using HotelKeeper.Models;
using HotelKeeper.Services;

namespace HotelKeeper.Data;

public static class DevelopmentSampleData
{
    public static async Task SeedAsync()
    {
        await using var db = await Db.BeginAsync();

        var adminId = await db.ScalarAsync<int>("SELECT Id FROM Users WHERE Username='admin'");
        var recId = await db.ScalarAsync<int>("SELECT Id FROM Users WHERE Username='reception'");
        var hkId = await db.ScalarAsync<int>("SELECT Id FROM Users WHERE Username='housekeeper'");
        var guestRoleId = await db.ScalarAsync<int>("SELECT Id FROM Roles WHERE Name='Guest'");

        for (var floor = 1; floor <= 4; floor++)
        {
            for (var i = 1; i <= 4; i++)
            {
                var num = $"{floor}0{i}";
                var typeName = floor == 4 ? "Family Suite" : i <= 2 ? "Standard" : "Deluxe";
                var typeId = await db.ScalarAsync<int>("SELECT Id FROM RoomTypes WHERE Name=@n", new { n = typeName });
                var cap = floor == 4 ? 5 : i <= 2 ? 2 : 3;
                var rate = floor == 4 ? 6200m : i <= 2 ? 2500m : 3800m;

                var exists = await db.ScalarAsync<int>("SELECT COUNT(*) FROM Rooms WHERE RoomNumber=@n", new { n = num });
                if (exists == 0)
                {
                    await db.ExecAsync(
                        "INSERT INTO Rooms (RoomNumber, Building, `Floor`, RoomTypeId, Capacity, Rate, Amenities, Description, Status, UpdatedAt) " +
                        "VALUES (@num, 'Main', @fl, @t, @c, @r, 'Air conditioning, Wi-Fi, TV, Private bathroom', 'Comfortable guest room', 'Available', UTC_TIMESTAMP())",
                        new { num, fl = floor, t = typeId, c = cap, r = rate });
                }
            }
        }

        var sampleGuests = new[]
        {
            ("Maria Santos", "maria.santos@sample.local", "0917-100-0001", "123 Orchard Rd, Manila", "Filipino", "PASSPORT-PH101", "mariasantos"),
            ("James Wilson", "james.wilson@sample.local", "+1-555-0102", "45 Elm Street, California", "American", "DL-CA20441", "jameswilson"),
            ("Kenji Sato", "kenji.sato@sample.local", "+81-90-1234-5678", "2-4-8 Shibuya, Tokyo", "Japanese", "PASS-JP8819", "kenjisato"),
            ("Chloe Tremblay", "chloe.t@sample.local", "+1-416-555-0199", "88 Queen St, Toronto", "Canadian", "PASS-CA4091", "chloetremblay"),
            ("Liam O'Connor", "liam.oc@sample.local", "+353-87-555-1212", "12 Grafton St, Dublin", "Irish", "PASS-IE9102", "liamoconnor"),
            ("Ananya Sharma", "ananya.s@sample.local", "+91-98200-11223", "5 Bandra West, Mumbai", "Indian", "PASS-IN5541", "ananyasharma"),
            ("Mateo Rossi", "mateo.rossi@sample.local", "+39-06-698-1234", "Via del Corso 22, Rome", "Italian", "PASS-IT1982", "mateorossi"),
            ("Emma Watson", "emma.w@sample.local", "+44-7700-900123", "14 Kensington Rd, London", "British", "PASS-GB6621", "emmawatson"),
            ("Carlos Garcia", "carlos.g@sample.local", "+34-600-123-456", "Gran Via 50, Madrid", "Spanish", "PASS-ES7710", "carlosgarcia"),
            ("Lars Lindqvist", "lars.l@sample.local", "+46-70-123-4567", "Storgatan 10, Stockholm", "Swedish", "PASS-SE3312", "larslindqvist"),
            ("Fatima Al-Mansoor", "fatima.m@sample.local", "+971-50-123-4567", "Corniche Rd, Abu Dhabi", "Emirati", "PASS-AE8890", "fatimamansoor"),
            ("David Kim", "david.kim@sample.local", "+82-10-5555-6666", "Teheran-ro, Seoul", "South Korean", "PASS-KR2219", "davidkim")
        };

        foreach (var (fn, em, ph, addr, nat, idDoc, un) in sampleGuests)
        {
            var userExists = await db.ScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE Username=@u", new { u = un });
            int uid;
            if (userExists == 0)
            {
                uid = await db.InsertAsync(
                    "INSERT INTO Users (Username, FullName, Email, Phone, PasswordHash, RoleId, MustChangePassword, CreatedAt) " +
                    "VALUES (@u, @f, @e, @p, @h, @r, 0, UTC_TIMESTAMP())",
                    new { u = un, f = fn, e = em, p = ph, h = Passwords.Hash("Guest12345"), r = guestRoleId });
            }
            else
            {
                uid = await db.ScalarAsync<int>("SELECT Id FROM Users WHERE Username=@u", new { u = un });
            }

            var guestExists = await db.ScalarAsync<int>("SELECT COUNT(*) FROM Guests WHERE UserId=@u", new { u = uid });
            if (guestExists == 0)
            {
                await db.InsertAsync(
                    "INSERT INTO Guests (UserId, FullName, Email, Phone, Address, Nationality, IdDocument, CreatedById, CreatedAt) " +
                    "VALUES (@u, @f, @e, @p, @a, @n, @i, @c, UTC_TIMESTAMP())",
                    new { u = uid, f = fn, e = em, p = ph, a = addr, n = nat, i = idDoc, c = recId });
            }
        }

        var stayPlan = new (string roomNum, string username, int inOffset, int outOffset, bool active)[]
        {
            ("101", "mariasantos", -2, 2, true),
            ("102", "jameswilson", -1, 3, true),
            ("103", "kenjisato", -3, 1, true),
            ("104", "chloetremblay", 0, 4, true),
            ("201", "liamoconnor", -2, 2, true),
            ("202", "ananyasharma", -1, 1, true),
            ("203", "mateorossi", -10, -5, false),
            ("204", "emmawatson", -8, -3, false),
            ("301", "carlosgarcia", -7, -2, false),
            ("302", "larslindqvist", -5, -1, false),
            ("303", "fatimamansoor", -12, -7, false),
            ("304", "davidkim", -15, -10, false)
        };

        var today = DateTime.Today;

        foreach (var (rNum, uName, inOff, outOff, isActive) in stayPlan)
        {
            var guestId = await db.ScalarAsync<int>("SELECT g.Id FROM Guests g JOIN Users u ON u.Id=g.UserId WHERE u.Username=@u", new { u = uName });
            var room = await db.FirstAsync<RoomMini>("SELECT Id, Rate FROM Rooms WHERE RoomNumber=@n", new { n = rNum });
            if (room is null) continue;

            var checkIn = today.AddDays(inOff);
            var checkOut = today.AddDays(outOff);
            var nights = (checkOut - checkIn).Days;
            var total = room.Rate * nights;
            var resStatus = isActive ? ResStatus.CheckedIn : ResStatus.CheckedOut;

            var existingRes = await db.ScalarAsync<int?>(
                "SELECT r.Id FROM Reservations r WHERE r.GuestId=@g AND r.RoomId=@r AND r.CheckInDate=@in",
                new { g = guestId, r = room.Id, @in = checkIn });

            int resId;
            if (existingRes is null || existingRes == 0)
            {
                resId = await db.InsertAsync(
                    "INSERT INTO Reservations (GuestId, RoomId, CheckInDate, CheckOutDate, GuestCount, Status, NightlyRate, TotalAmount, Notes, CreatedById, CreatedAt, UpdatedAt) " +
                    "VALUES (@g, @r, @in, @out, 2, @st, @rate, @total, 'Sample booking', @u, UTC_TIMESTAMP(), UTC_TIMESTAMP())",
                    new { g = guestId, r = room.Id, @in = checkIn, @out = checkOut, st = resStatus, rate = room.Rate, total, u = recId });
            }
            else
            {
                resId = existingRes.Value;
            }

            var existingStay = await db.ScalarAsync<int?>("SELECT Id FROM Stays WHERE ReservationId=@r", new { r = resId });
            int stayId;
            if (existingStay is null || existingStay == 0)
            {
                stayId = await db.InsertAsync(
                    "INSERT INTO Stays (ReservationId, GuestId, RoomId, CheckInAt, ExpectedCheckOutDate, CheckOutAt, CheckedInById, CheckedOutById) " +
                    "VALUES (@res, @g, @r, @inAt, @out, @outAt, @inBy, @outBy)",
                    new
                    {
                        res = resId,
                        g = guestId,
                        r = room.Id,
                        inAt = checkIn.ToUniversalTime(),
                        @out = checkOut,
                        outAt = isActive ? (DateTime?)null : checkOut.ToUniversalTime(),
                        inBy = recId,
                        outBy = isActive ? (int?)null : recId
                    });
            }
            else
            {
                stayId = existingStay.Value;
            }

            var paymentCount = await db.ScalarAsync<int>("SELECT COUNT(*) FROM Payments WHERE ReservationId=@r", new { r = resId });
            if (paymentCount == 0)
            {
                var payAmount = isActive ? total * 0.5m : total;
                await db.InsertAsync(
                    "INSERT INTO Payments (ReservationId, Amount, Method, Status, Reference, RecordedById, CreatedAt) " +
                    "VALUES (@r, @a, 'Card', 'Paid', 'REF-SAMPLE-PAY', @u, UTC_TIMESTAMP())",
                    new { r = resId, a = payAmount, u = recId });
            }

            if (isActive)
            {
                await db.ExecAsync("UPDATE Rooms SET Status='Occupied' WHERE Id=@r", new { r = room.Id });
            }
        }

        var r401 = await db.ScalarAsync<int>("SELECT Id FROM Rooms WHERE RoomNumber='401'");
        var r402 = await db.ScalarAsync<int>("SELECT Id FROM Rooms WHERE RoomNumber='402'");
        var r403 = await db.ScalarAsync<int>("SELECT Id FROM Rooms WHERE RoomNumber='403'");
        var r404 = await db.ScalarAsync<int>("SELECT Id FROM Rooms WHERE RoomNumber='404'");

        if (r401 > 0) await db.ExecAsync("UPDATE Rooms SET Status='Dirty' WHERE Id=@r AND Status<>'Occupied'", new { r = r401 });
        if (r402 > 0) await db.ExecAsync("UPDATE Rooms SET Status='Cleaning' WHERE Id=@r AND Status<>'Occupied'", new { r = r402 });
        if (r403 > 0) await db.ExecAsync("UPDATE Rooms SET Status='OutOfService', StatusReason='HVAC replacement under warranty' WHERE Id=@r AND Status<>'Occupied'", new { r = r403 });
        if (r404 > 0) await db.ExecAsync("UPDATE Rooms SET Status='Available' WHERE Id=@r AND Status<>'Occupied'", new { r = r404 });

        var sampleTasks = new (string roomNum, string taskType, string priority, string status, string notes)[]
        {
            ("401", "Turnover", "High", WorkStatus.Pending, "Full turnover for next arrival."),
            ("402", "Routine", "Normal", WorkStatus.InProgress, "Mid-stay light cleaning in progress."),
            ("101", "Turnover", "Normal", WorkStatus.Completed, "Bed linen changed and bathroom disinfected."),
            ("102", "DeepClean", "Low", WorkStatus.Completed, "Quarterly carpet shampoo and curtain wash."),
            ("103", "Inspection", "High", WorkStatus.Completed, "Supervisor readiness inspection completed."),
            ("201", "Routine", "Normal", WorkStatus.Completed, "Trash emptied and fresh towels supplied."),
            ("202", "Turnover", "Normal", WorkStatus.Completed, "Standard turnover cleaning done."),
            ("203", "Turnover", "High", WorkStatus.Completed, "Check-out sanitization complete."),
            ("204", "Inspection", "Normal", WorkStatus.Completed, "Room inventory check passed."),
            ("301", "Routine", "Low", WorkStatus.Completed, "Replenished coffee station and water bottles."),
            ("302", "Turnover", "Normal", WorkStatus.Completed, "Deep clean after extended departure."),
            ("303", "Turnover", "Normal", WorkStatus.Completed, "Cleaned and prepared.")
        };

        foreach (var (rNum, tType, pri, st, notes) in sampleTasks)
        {
            var roomId = await db.ScalarAsync<int>("SELECT Id FROM Rooms WHERE RoomNumber=@n", new { n = rNum });
            var count = await db.ScalarAsync<int>("SELECT COUNT(*) FROM HousekeepingTasks WHERE RoomId=@r AND TaskType=@t AND Notes=@notes",
                new { r = roomId, t = tType, notes });
            if (count == 0 && roomId > 0)
            {
                var assigned = st is WorkStatus.InProgress or WorkStatus.Completed ? hkId : (int?)null;
                var started = st is WorkStatus.InProgress or WorkStatus.Completed ? DateTime.UtcNow.AddHours(-1) : (DateTime?)null;
                var completed = st == WorkStatus.Completed ? DateTime.UtcNow.AddMinutes(-10) : (DateTime?)null;

                var taskId = await db.InsertAsync(
                    "INSERT INTO HousekeepingTasks (RoomId, TaskType, Priority, Status, AssignedToId, Notes, CreatedById, CreatedAt, StartedAt, CompletedAt) " +
                    "VALUES (@r, @t, @pri, @st, @who, @notes, @creator, UTC_TIMESTAMP(), @started, @completed)",
                    new { r = roomId, t = tType, pri, st, who = assigned, notes, creator = recId, started, completed });

                var checklistItems = await db.QueryAsync<ChecklistTemplateRow>("SELECT Id, Label, SortOrder, IsActive FROM ChecklistTemplate WHERE IsActive=1");
                foreach (var item in checklistItems)
                {
                    var isDone = st == WorkStatus.Completed;
                    await db.ExecAsync(
                        "INSERT INTO TaskChecklist (TaskId, Label, IsDone, DoneAt) VALUES (@t, @l, @d, @at)",
                        new { t = taskId, l = item.Label, d = isDone ? 1 : 0, at = isDone ? DateTime.UtcNow : (DateTime?)null });
                }
            }
        }

        var sampleRequests = new (string roomNum, string username, string cat, string desc, string pri, string st, string res)[]
        {
            ("101", "mariasantos", Category.AdditionalItems, "Extra 2 sets of bath towels please", "Normal", ReqStatus.Fulfilled, "Delivered by housekeeping."),
            ("102", "jameswilson", Category.Cleaning, "Room make up requested before noon", "Normal", ReqStatus.InProgress, ""),
            ("103", "kenjisato", Category.GeneralAssistance, "Luggage assistance required at checkout", "Normal", ReqStatus.Pending, ""),
            ("104", "chloetremblay", Category.AdditionalItems, "Extra pillows and blanket", "Low", ReqStatus.Pending, ""),
            ("201", "liamoconnor", Category.Maintenance, "Shower drain draining slower than usual", "Normal", ReqStatus.Acknowledged, "Assigned to maintenance."),
            ("202", "ananyasharma", Category.Cleaning, "Spilled water on bedside rug, needs drying", "High", ReqStatus.Fulfilled, "Rug replaced and floor dried."),
            ("101", "mariasantos", Category.GeneralAssistance, "Late check-out inquiry", "Low", ReqStatus.Fulfilled, "Approved 1 hour complimentary extension."),
            ("102", "jameswilson", Category.AdditionalItems, "Extra drinking water bottles", "Normal", ReqStatus.Fulfilled, "2 cold bottles delivered."),
            ("103", "kenjisato", Category.Cleaning, "Fresh bed sheets requested", "Normal", ReqStatus.Fulfilled, "Sheets replaced."),
            ("104", "chloetremblay", Category.GeneralAssistance, "Directions to nearest pharmacy", "Low", ReqStatus.Fulfilled, "Concierge map provided."),
            ("201", "liamoconnor", Category.AdditionalItems, "Dental kit and shaving kit", "Normal", ReqStatus.Fulfilled, "Delivered."),
            ("202", "ananyasharma", Category.AdditionalItems, "Iron and ironing board", "Normal", ReqStatus.Fulfilled, "Delivered to room wardrobe.")
        };

        foreach (var (rNum, uName, cat, desc, pri, st, res) in sampleRequests)
        {
            var guestId = await db.ScalarAsync<int>("SELECT g.Id FROM Guests g JOIN Users u ON u.Id=g.UserId WHERE u.Username=@u", new { u = uName });
            var roomId = await db.ScalarAsync<int>("SELECT Id FROM Rooms WHERE RoomNumber=@n", new { n = rNum });
            var stayId = await db.ScalarAsync<int?>("SELECT Id FROM Stays WHERE GuestId=@g AND RoomId=@r ORDER BY Id DESC LIMIT 1", new { g = guestId, r = roomId });

            if (guestId > 0 && roomId > 0 && stayId is not null)
            {
                var exists = await db.ScalarAsync<int>("SELECT COUNT(*) FROM Requests WHERE GuestId=@g AND Description=@d", new { g = guestId, d = desc });
                if (exists == 0)
                {
                    var reqId = await db.InsertAsync(
                        "INSERT INTO Requests (GuestId, StayId, RoomId, Category, Description, Priority, Status, AssignedToId, Resolution, CreatedAt, UpdatedAt) " +
                        "VALUES (@g, @st, @r, @cat, @d, @pri, @status, @who, @res, UTC_TIMESTAMP(), UTC_TIMESTAMP())",
                        new { g = guestId, st = stayId.Value, r = roomId, cat, d = desc, pri, status = st, who = hkId, res = string.IsNullOrEmpty(res) ? null : res });

                    if (cat == Category.AdditionalItems)
                    {
                        var towelItem = await db.ScalarAsync<int>("SELECT Id FROM RequestableItems WHERE Name='Towels' LIMIT 1");
                        if (towelItem > 0)
                        {
                            await db.ExecAsync(
                                "INSERT INTO RequestItems (RequestId, ItemId, ItemName, Quantity) VALUES (@q, @i, 'Towels', 2)",
                                new { q = reqId, i = towelItem });
                        }
                    }
                }
            }
        }

        var sampleReports = new (string roomNum, string issue, string desc, bool blocks, string st, string res)[]
        {
            ("403", "Air conditioning", "AC compressor failing; parts ordered under warranty.", true, ReportStatus.Open, ""),
            ("101", "Plumbing", "Sink tap dripping slightly.", false, ReportStatus.Assigned, ""),
            ("102", "Electrical", "Desk lamp socket loose.", false, ReportStatus.InProgress, ""),
            ("103", "Bathroom", "Shower head descaling required.", false, ReportStatus.Resolved, "Replaced shower head and tested flow rate."),
            ("104", "Furniture", "Wardrobe hinge lubricated and adjusted.", false, ReportStatus.Closed, "Tightened hinges and adjusted door alignment."),
            ("201", "Plumbing", "Shower drain slow, cleared obstruction.", false, ReportStatus.Closed, "Cleared drain pipe, water draining normally."),
            ("202", "Lighting", "Bedside reading LED bulb flicker.", false, ReportStatus.Closed, "Replaced bulb."),
            ("203", "TV / Remote", "Remote control battery replaced.", false, ReportStatus.Closed, "New batteries fitted."),
            ("204", "Safe", "Electronic safe passcode reset.", false, ReportStatus.Closed, "Master key reset performed."),
            ("301", "Wi-Fi", "Access point signal check.", false, ReportStatus.Closed, "Router rebooted and signal verified."),
            ("302", "Door / Lock", "Keycard reader cleaned and tested.", false, ReportStatus.Closed, "Sensor cleaned."),
            ("303", "Air conditioning", "Filter cleaned and airflow restored.", false, ReportStatus.Closed, "Filter washed and dried.")
        };

        foreach (var (rNum, issue, desc, blocks, st, res) in sampleReports)
        {
            var roomId = await db.ScalarAsync<int>("SELECT Id FROM Rooms WHERE RoomNumber=@n", new { n = rNum });
            if (roomId > 0)
            {
                var exists = await db.ScalarAsync<int>("SELECT COUNT(*) FROM MaintenanceReports WHERE RoomId=@r AND IssueType=@issue", new { r = roomId, issue });
                if (exists == 0)
                {
                    await db.InsertAsync(
                        "INSERT INTO MaintenanceReports (RoomId, ReportedById, Source, IssueType, Description, BlocksOccupancy, Status, AssignedToId, Resolution, CreatedAt, UpdatedAt) " +
                        "VALUES (@r, @u, 'Staff', @issue, @desc, @b, @st, @who, @res, UTC_TIMESTAMP(), UTC_TIMESTAMP())",
                        new { r = roomId, u = hkId, issue, desc, b = blocks ? 1 : 0, st, who = hkId, res = string.IsNullOrEmpty(res) ? null : res });
                }
            }
        }

        await db.CommitAsync();
    }

    sealed record RoomMini(int Id, decimal Rate);
}
