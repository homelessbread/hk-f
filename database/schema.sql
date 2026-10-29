
CREATE DATABASE IF NOT EXISTS hotelkeeper CHARACTER SET utf8mb4;
USE hotelkeeper;

CREATE TABLE IF NOT EXISTS Roles (
    Id   INT AUTO_INCREMENT PRIMARY KEY,
    Name VARCHAR(30) NOT NULL UNIQUE
);

CREATE TABLE IF NOT EXISTS Users (
    Id                 INT AUTO_INCREMENT PRIMARY KEY,
    Username           VARCHAR(50)  NOT NULL UNIQUE,
    FullName           VARCHAR(100) NOT NULL,
    Email              VARCHAR(120) NOT NULL DEFAULT '',
    Phone              VARCHAR(40)  NOT NULL DEFAULT '',
    PasswordHash       VARCHAR(255) NOT NULL,
    RoleId             INT          NOT NULL,
    IsActive           TINYINT(1)   NOT NULL DEFAULT 1,
    MustChangePassword TINYINT(1)   NOT NULL DEFAULT 0,
    CreatedAt          DATETIME     NOT NULL DEFAULT (UTC_TIMESTAMP()),
    LastLoginAt        DATETIME     NULL,
    FOREIGN KEY (RoleId) REFERENCES Roles (Id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS Guests (
    Id         INT AUTO_INCREMENT PRIMARY KEY,
    UserId     INT          NOT NULL UNIQUE,
    FullName   VARCHAR(100) NOT NULL,
    Email      VARCHAR(120) NOT NULL DEFAULT '',
    Phone      VARCHAR(40)  NOT NULL DEFAULT '',
    Address    VARCHAR(255) NOT NULL DEFAULT '',
    Nationality VARCHAR(60) NOT NULL DEFAULT '',
    IdDocument VARCHAR(80)  NOT NULL DEFAULT '',
    Notes      VARCHAR(255) NOT NULL DEFAULT '',
    IsArchived TINYINT(1)   NOT NULL DEFAULT 0,
    CreatedAt  DATETIME     NOT NULL DEFAULT (UTC_TIMESTAMP()),
    CreatedById INT         NULL,
    FOREIGN KEY (UserId)      REFERENCES Users (Id) ON DELETE RESTRICT,
    FOREIGN KEY (CreatedById) REFERENCES Users (Id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS RoomTypes (
    Id          INT AUTO_INCREMENT PRIMARY KEY,
    Name        VARCHAR(40)   NOT NULL UNIQUE,
    Description VARCHAR(255)  NOT NULL DEFAULT '',
    BaseRate    DECIMAL(10,2) NOT NULL,
    Capacity    INT           NOT NULL,
    IsActive    TINYINT(1)    NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS Rooms (
    Id          INT AUTO_INCREMENT PRIMARY KEY,
    RoomNumber  VARCHAR(10)   NOT NULL UNIQUE,
    Building    VARCHAR(40)   NOT NULL DEFAULT 'Main',
    `Floor`     INT           NOT NULL,
    RoomTypeId  INT           NOT NULL,
    Capacity    INT           NOT NULL,
    Rate        DECIMAL(10,2) NOT NULL,
    Amenities   VARCHAR(255)  NOT NULL DEFAULT '',
    Description VARCHAR(255)  NOT NULL DEFAULT '',
    Status      VARCHAR(20)   NOT NULL DEFAULT 'Available',
    StatusReason VARCHAR(255) NULL,
    IsArchived  TINYINT(1)    NOT NULL DEFAULT 0,
    Version     INT           NOT NULL DEFAULT 0,
    UpdatedAt   DATETIME      NOT NULL DEFAULT (UTC_TIMESTAMP()),
    FOREIGN KEY (RoomTypeId) REFERENCES RoomTypes (Id) ON DELETE RESTRICT,
    CHECK (Status IN ('Available','Occupied','Dirty','Cleaning','OutOfService'))
);

CREATE TABLE IF NOT EXISTS RoomItems (
    Id                 INT AUTO_INCREMENT PRIMARY KEY,
    RoomId             INT          NOT NULL,
    ItemName           VARCHAR(60)  NOT NULL,
    Quantity           INT          NOT NULL DEFAULT 1,
    ConditionStatus    VARCHAR(20)  NOT NULL DEFAULT 'Good',
    NeedsReplenishment TINYINT(1)   NOT NULL DEFAULT 0,
    UpdatedAt          DATETIME     NOT NULL DEFAULT (UTC_TIMESTAMP()),
    FOREIGN KEY (RoomId) REFERENCES Rooms (Id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS Reservations (
    Id            INT AUTO_INCREMENT PRIMARY KEY,
    GuestId       INT           NOT NULL,
    RoomId        INT           NOT NULL,
    CheckInDate   DATE          NOT NULL,
    CheckOutDate  DATE          NOT NULL,
    GuestCount    INT           NOT NULL,
    Status        VARCHAR(20)   NOT NULL DEFAULT 'Pending',
    NightlyRate   DECIMAL(10,2) NOT NULL,
    TotalAmount   DECIMAL(10,2) NOT NULL,
    Notes         VARCHAR(255)  NOT NULL DEFAULT '',
    CreatedById   INT           NOT NULL,
    CreatedAt     DATETIME      NOT NULL DEFAULT (UTC_TIMESTAMP()),
    UpdatedAt     DATETIME      NOT NULL DEFAULT (UTC_TIMESTAMP()),
    CancelledAt   DATETIME      NULL,
    CancelReason  VARCHAR(255)  NULL,
    FOREIGN KEY (GuestId)     REFERENCES Guests (Id) ON DELETE RESTRICT,
    FOREIGN KEY (RoomId)      REFERENCES Rooms (Id)  ON DELETE RESTRICT,
    FOREIGN KEY (CreatedById) REFERENCES Users (Id)  ON DELETE RESTRICT,
    CHECK (CheckOutDate > CheckInDate),
    CHECK (GuestCount > 0),
    CHECK (Status IN ('Pending','Confirmed','CheckedIn','CheckedOut','Cancelled')),
    INDEX ix_res_room_dates (RoomId, CheckInDate, CheckOutDate),
    INDEX ix_res_guest (GuestId),
    INDEX ix_res_status (Status)
);

-- A stay is the real occupation of a room by a guest. ActiveRoomId / ActiveGuestId are NULL once the
-- stay has ended, so the unique keys guarantee at most one active stay per room and per guest even if
-- two receptionists act at the same moment.
CREATE TABLE IF NOT EXISTS Stays (
    Id              INT AUTO_INCREMENT PRIMARY KEY,
    ReservationId   INT      NOT NULL UNIQUE,
    GuestId         INT      NOT NULL,
    RoomId          INT      NOT NULL,
    CheckInAt       DATETIME NOT NULL DEFAULT (UTC_TIMESTAMP()),
    ExpectedCheckOutDate DATE NOT NULL,
    ExpectedCheckOutAt  DATETIME NULL,
    CheckOutAt      DATETIME NULL,
    CheckedInById   INT      NOT NULL,
    CheckedOutById  INT      NULL,
    ActiveRoomId    INT GENERATED ALWAYS AS (IF(CheckOutAt IS NULL, RoomId, NULL))  STORED,
    ActiveGuestId   INT GENERATED ALWAYS AS (IF(CheckOutAt IS NULL, GuestId, NULL)) STORED,
    FOREIGN KEY (ReservationId) REFERENCES Reservations (Id) ON DELETE RESTRICT,
    FOREIGN KEY (GuestId)       REFERENCES Guests (Id)       ON DELETE RESTRICT,
    FOREIGN KEY (RoomId)        REFERENCES Rooms (Id)        ON DELETE RESTRICT,
    FOREIGN KEY (CheckedInById) REFERENCES Users (Id)        ON DELETE RESTRICT,
    FOREIGN KEY (CheckedOutById) REFERENCES Users (Id)       ON DELETE RESTRICT,
    UNIQUE KEY uq_active_room (ActiveRoomId),
    UNIQUE KEY uq_active_guest (ActiveGuestId)
);

CREATE TABLE IF NOT EXISTS Payments (
    Id            INT AUTO_INCREMENT PRIMARY KEY,
    ReservationId INT           NOT NULL,
    Amount        DECIMAL(10,2) NOT NULL,
    Method        VARCHAR(20)   NOT NULL,
    Status        VARCHAR(20)   NOT NULL DEFAULT 'Paid',
    Reference     VARCHAR(80)   NOT NULL DEFAULT '',
    RecordedById  INT           NOT NULL,
    CreatedAt     DATETIME      NOT NULL DEFAULT (UTC_TIMESTAMP()),
    FOREIGN KEY (ReservationId) REFERENCES Reservations (Id) ON DELETE RESTRICT,
    FOREIGN KEY (RecordedById)  REFERENCES Users (Id)        ON DELETE RESTRICT,
    CHECK (Amount > 0)
);

CREATE TABLE IF NOT EXISTS HotelServices (
    Id          INT AUTO_INCREMENT PRIMARY KEY,
    Name        VARCHAR(80)   NOT NULL UNIQUE,
    Description VARCHAR(255)  NOT NULL DEFAULT '',
    Price       DECIMAL(10,2) NOT NULL DEFAULT 0,
    IsActive    TINYINT(1)    NOT NULL DEFAULT 1
);

-- Items guests may request, and the rules for fulfilling them.
CREATE TABLE IF NOT EXISTS RequestableItems (
    Id           INT AUTO_INCREMENT PRIMARY KEY,
    Name         VARCHAR(60)  NOT NULL UNIQUE,
    ItemCategory VARCHAR(40)  NOT NULL DEFAULT 'Supplies',
    MaxQuantity  INT          NOT NULL DEFAULT 4,
    Rule         VARCHAR(255) NOT NULL DEFAULT '',
    IsActive     TINYINT(1)   NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS Requests (
    Id              INT AUTO_INCREMENT PRIMARY KEY,
    GuestId         INT          NOT NULL,
    StayId          INT          NOT NULL,
    RoomId          INT          NOT NULL,
    Category        VARCHAR(30)  NOT NULL,
    Description     VARCHAR(500) NOT NULL,
    Priority        VARCHAR(10)  NOT NULL DEFAULT 'Normal',
    Status          VARCHAR(20)  NOT NULL DEFAULT 'Pending',
    AssignedToId    INT          NULL,
    Resolution      VARCHAR(500) NULL,
    IsArchived      TINYINT(1)   NOT NULL DEFAULT 0,
    CreatedAt       DATETIME     NOT NULL DEFAULT (UTC_TIMESTAMP()),
    UpdatedAt       DATETIME     NOT NULL DEFAULT (UTC_TIMESTAMP()),
    ResolvedAt      DATETIME     NULL,
    FOREIGN KEY (GuestId)      REFERENCES Guests (Id) ON DELETE RESTRICT,
    FOREIGN KEY (StayId)       REFERENCES Stays (Id)  ON DELETE RESTRICT,
    FOREIGN KEY (RoomId)       REFERENCES Rooms (Id)  ON DELETE RESTRICT,
    FOREIGN KEY (AssignedToId) REFERENCES Users (Id)  ON DELETE RESTRICT,
    CHECK (Category IN ('Maintenance','Cleaning','AdditionalItems','GeneralAssistance')),
    CHECK (Status IN ('Pending','Acknowledged','InProgress','Fulfilled','Rejected','UnableToFulfill')),
    INDEX ix_req_status (Status),
    INDEX ix_req_guest (GuestId)
);

CREATE TABLE IF NOT EXISTS RequestItems (
    Id        INT AUTO_INCREMENT PRIMARY KEY,
    RequestId INT         NOT NULL,
    ItemId    INT         NOT NULL,
    ItemName  VARCHAR(60) NOT NULL,
    Quantity  INT         NOT NULL,
    FOREIGN KEY (RequestId) REFERENCES Requests (Id)        ON DELETE RESTRICT,
    FOREIGN KEY (ItemId)    REFERENCES RequestableItems (Id) ON DELETE RESTRICT,
    CHECK (Quantity > 0)
);

CREATE TABLE IF NOT EXISTS MaintenanceReports (
    Id               INT AUTO_INCREMENT PRIMARY KEY,
    RoomId           INT          NOT NULL,
    RequestId        INT          NULL,
    ReportedById     INT          NOT NULL,
    Source           VARCHAR(20)  NOT NULL,
    IssueType        VARCHAR(40)  NOT NULL,
    Description      VARCHAR(500) NOT NULL,
    BlocksOccupancy  TINYINT(1)   NOT NULL DEFAULT 0,
    Status           VARCHAR(20)  NOT NULL DEFAULT 'Open',
    AssignedToId     INT          NULL,
    Resolution       VARCHAR(500) NULL,
    IsArchived       TINYINT(1)   NOT NULL DEFAULT 0,
    CreatedAt        DATETIME     NOT NULL DEFAULT (UTC_TIMESTAMP()),
    UpdatedAt        DATETIME     NOT NULL DEFAULT (UTC_TIMESTAMP()),
    ResolvedAt       DATETIME     NULL,
    ClosedAt         DATETIME     NULL,
    FOREIGN KEY (RoomId)       REFERENCES Rooms (Id)    ON DELETE RESTRICT,
    FOREIGN KEY (RequestId)    REFERENCES Requests (Id) ON DELETE RESTRICT,
    FOREIGN KEY (ReportedById) REFERENCES Users (Id)    ON DELETE RESTRICT,
    FOREIGN KEY (AssignedToId) REFERENCES Users (Id)    ON DELETE RESTRICT,
    CHECK (Status IN ('Open','Assigned','InProgress','Resolved','Closed')),
    INDEX ix_mr_room_status (RoomId, Status)
);

CREATE TABLE IF NOT EXISTS ChecklistTemplate (
    Id        INT AUTO_INCREMENT PRIMARY KEY,
    Label     VARCHAR(100) NOT NULL UNIQUE,
    SortOrder INT          NOT NULL DEFAULT 0,
    IsActive  TINYINT(1)   NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS HousekeepingTasks (
    Id           INT AUTO_INCREMENT PRIMARY KEY,
    RoomId       INT          NOT NULL,
    TaskType     VARCHAR(20)  NOT NULL,
    Priority     VARCHAR(10)  NOT NULL DEFAULT 'Normal',
    Status       VARCHAR(20)  NOT NULL DEFAULT 'Pending',
    AssignedToId INT          NULL,
    RequestId    INT          NULL,
    Notes        VARCHAR(255) NOT NULL DEFAULT '',
    CreatedById  INT          NOT NULL,
    CreatedAt    DATETIME     NOT NULL DEFAULT (UTC_TIMESTAMP()),
    StartedAt    DATETIME     NULL,
    CompletedAt  DATETIME     NULL,
    FOREIGN KEY (RoomId)       REFERENCES Rooms (Id)    ON DELETE RESTRICT,
    FOREIGN KEY (AssignedToId) REFERENCES Users (Id)    ON DELETE RESTRICT,
    FOREIGN KEY (RequestId)    REFERENCES Requests (Id) ON DELETE RESTRICT,
    FOREIGN KEY (CreatedById)  REFERENCES Users (Id)    ON DELETE RESTRICT,
    CHECK (Status IN ('Pending','InProgress','Completed','Cancelled')),
    INDEX ix_task_status (Status)
);

CREATE TABLE IF NOT EXISTS TaskChecklist (
    Id      INT AUTO_INCREMENT PRIMARY KEY,
    TaskId  INT          NOT NULL,
    Label   VARCHAR(100) NOT NULL,
    IsDone  TINYINT(1)   NOT NULL DEFAULT 0,
    DoneAt  DATETIME     NULL,
    FOREIGN KEY (TaskId) REFERENCES HousekeepingTasks (Id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS Notifications (
    Id         INT AUTO_INCREMENT PRIMARY KEY,
    UserId     INT          NOT NULL,
    Title      VARCHAR(100) NOT NULL,
    Message    VARCHAR(300) NOT NULL,
    EntityType VARCHAR(30)  NULL,
    EntityId   INT          NULL,
    IsRead     TINYINT(1)   NOT NULL DEFAULT 0,
    CreatedAt  DATETIME     NOT NULL DEFAULT (UTC_TIMESTAMP()),
    FOREIGN KEY (UserId) REFERENCES Users (Id) ON DELETE RESTRICT,
    INDEX ix_notif_user (UserId, IsRead)
);

CREATE TABLE IF NOT EXISTS RoomStatusHistory (
    Id             INT AUTO_INCREMENT PRIMARY KEY,
    RoomId         INT          NOT NULL,
    PreviousStatus VARCHAR(20)  NOT NULL,
    NewStatus      VARCHAR(20)  NOT NULL,
    ChangedById    INT          NOT NULL,
    Reason         VARCHAR(255) NULL,
    ChangedAt      DATETIME     NOT NULL DEFAULT (UTC_TIMESTAMP()),
    FOREIGN KEY (RoomId)      REFERENCES Rooms (Id) ON DELETE RESTRICT,
    FOREIGN KEY (ChangedById) REFERENCES Users (Id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS AuditLogs (
    Id         INT AUTO_INCREMENT PRIMARY KEY,
    UserId     INT          NULL,
    Action     VARCHAR(60)  NOT NULL,
    EntityType VARCHAR(30)  NOT NULL,
    EntityId   INT          NULL,
    Details    VARCHAR(500) NOT NULL DEFAULT '',
    CreatedAt  DATETIME     NOT NULL DEFAULT (UTC_TIMESTAMP()),
    FOREIGN KEY (UserId) REFERENCES Users (Id) ON DELETE RESTRICT,
    INDEX ix_audit_time (CreatedAt)
);

CREATE TABLE IF NOT EXISTS Settings (
    SettingKey   VARCHAR(50)  NOT NULL PRIMARY KEY,
    SettingValue VARCHAR(255) NOT NULL,
    Description  VARCHAR(255) NOT NULL DEFAULT ''
);
