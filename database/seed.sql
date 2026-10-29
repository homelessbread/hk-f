
USE hotelkeeper;

INSERT IGNORE INTO Settings (SettingKey, SettingValue) VALUES
 ('HotelName', 'Hotel Keeper'),
 ('HotelAddress', '1 Harbour Road'),
 ('HotelPhone', '+00 000 000 000'),
 ('Currency', '$'),
 ('CancellationHours', '24'),
 ('ChecklistTurnover', 'Change bed linens|Clean the bathroom|Replace towels|Replenish supplies|Vacuum and mop floor|Check room items and report damage'),
 ('ChecklistInStay', 'Tidy the room|Replace towels|Empty bins');

INSERT IGNORE INTO RoomTypes (Name, Description, Capacity, BaseRate, Amenities) VALUES
 ('Standard', 'Comfortable room with a queen bed and city view.', 2, 80.00, 'Wi-Fi, Air conditioning, TV, Desk'),
 ('Deluxe', 'Spacious room with a king bed, seating area and balcony.', 3, 120.00, 'Wi-Fi, Air conditioning, TV, Mini bar, Balcony'),
 ('Family Suite', 'Two bedrooms and a living area for families.', 5, 190.00, 'Wi-Fi, Air conditioning, TV, Kitchenette, Two bathrooms'),
 ('Executive Suite', 'Top-floor suite with lounge and premium bathroom.', 2, 260.00, 'Wi-Fi, Air conditioning, TV, Lounge, Bathtub, Coffee machine');

INSERT IGNORE INTO Rooms (RoomNumber, Building, Floor, RoomTypeId, Capacity, Rate, Amenities, Description)
SELECT v.n, 'Main', v.f, t.Id, t.Capacity, t.BaseRate, t.Amenities, t.Description
FROM (SELECT '101' n, 1 f, 'Standard' tn UNION ALL SELECT '102', 1, 'Standard' UNION ALL SELECT '103', 1, 'Standard'
      UNION ALL SELECT '201', 2, 'Deluxe' UNION ALL SELECT '202', 2, 'Deluxe' UNION ALL SELECT '203', 2, 'Deluxe'
      UNION ALL SELECT '301', 3, 'Family Suite' UNION ALL SELECT '302', 3, 'Family Suite'
      UNION ALL SELECT '401', 4, 'Executive Suite' UNION ALL SELECT '402', 4, 'Executive Suite') v
JOIN RoomTypes t ON t.Name = v.tn;

INSERT IGNORE INTO RoomItems (RoomId, ItemName, Quantity)
SELECT r.Id, i.n, i.q FROM Rooms r
JOIN (SELECT 'Bath towel' n, 2 q UNION ALL SELECT 'Pillow', 4 UNION ALL SELECT 'Blanket', 2 UNION ALL SELECT 'Television', 1 UNION ALL SELECT 'Hair dryer', 1) i
WHERE NOT EXISTS (SELECT 1 FROM RoomItems x WHERE x.RoomId = r.Id);

INSERT IGNORE INTO HotelServices (Name, Description, Price) VALUES
 ('Breakfast buffet', 'Served daily from 6:30 to 10:30 in the main restaurant.', 12.00),
 ('Airport transfer', 'Private car to or from the airport.', 35.00),
 ('Laundry service', 'Same-day laundry for items handed in before 10:00.', 15.00),
 ('Spa and wellness', 'Massage and sauna, by appointment.', 45.00),
 ('Late check-out', 'Keep your room until 16:00, subject to availability.', 20.00);

INSERT IGNORE INTO ItemCatalog (Name, Category, MaxQuantity, Rule) VALUES
 ('Extra pillow', 'Bedding', 3, ''),
 ('Extra blanket', 'Bedding', 2, ''),
 ('Bath towel', 'Bathroom', 4, ''),
 ('Tissues', 'Supplies', 3, ''),
 ('Toiletries kit', 'Bathroom', 2, ''),
 ('Drinking water', 'Supplies', 6, 'Delivered by housekeeping within 20 minutes.');
