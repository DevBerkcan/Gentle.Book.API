CREATE TABLE [__EFMigrationsHistory] (
    [MigrationId] nvarchar(150) NOT NULL,
    [ProductVersion] nvarchar(32) NOT NULL,
    CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
);

CREATE TABLE [LinkClicks] (
    [Id] uniqueidentifier NOT NULL,
    [LinkName] nvarchar(100) NOT NULL,
    [LinkUrl] nvarchar(500) NOT NULL,
    [ClickedAt] datetime2 NOT NULL,
    [SessionId] nvarchar(100) NULL,
    [ReferrerUrl] nvarchar(500) NULL,
    CONSTRAINT [PK_LinkClicks] PRIMARY KEY ([Id])
);

CREATE TABLE [PageViews] (
    [Id] uniqueidentifier NOT NULL,
    [PageUrl] nvarchar(500) NOT NULL,
    [ReferrerUrl] nvarchar(500) NULL,
    [UtmSource] nvarchar(max) NULL,
    [UtmMedium] nvarchar(max) NULL,
    [UtmCampaign] nvarchar(max) NULL,
    [UtmContent] nvarchar(max) NULL,
    [UtmTerm] nvarchar(max) NULL,
    [UserAgent] nvarchar(max) NULL,
    [IpAddress] nvarchar(max) NULL,
    [ViewedAt] datetime2 NOT NULL,
    [SessionId] nvarchar(450) NULL,
    CONSTRAINT [PK_PageViews] PRIMARY KEY ([Id])
);

CREATE TABLE [Tenants] (
    [Id] uniqueidentifier NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [Slug] nvarchar(100) NOT NULL,
    [IndustryType] nvarchar(50) NOT NULL,
    [IsActive] bit NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_Tenants] PRIMARY KEY ([Id])
);

CREATE TABLE [BusinessHours] (
    [Id] uniqueidentifier NOT NULL,
    [TenantId] uniqueidentifier NOT NULL,
    [DayOfWeek] int NOT NULL,
    [OpenTime] time NOT NULL,
    [CloseTime] time NOT NULL,
    [IsOpen] bit NOT NULL,
    [BreakStartTime] time NULL,
    [BreakEndTime] time NULL,
    CONSTRAINT [PK_BusinessHours] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_BusinessHours_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [Employees] (
    [Id] uniqueidentifier NOT NULL,
    [TenantId] uniqueidentifier NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [Role] nvarchar(100) NOT NULL,
    [Specialty] nvarchar(200) NULL,
    [IsActive] bit NOT NULL,
    [Location] nvarchar(max) NULL,
    [Username] nvarchar(max) NULL,
    [PasswordHash] nvarchar(max) NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_Employees] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Employees_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [PlatformUsers] (
    [Id] uniqueidentifier NOT NULL,
    [TenantId] uniqueidentifier NULL,
    [Email] nvarchar(255) NOT NULL,
    [PasswordHash] nvarchar(max) NOT NULL,
    [FirstName] nvarchar(max) NOT NULL,
    [LastName] nvarchar(max) NOT NULL,
    [Role] nvarchar(50) NOT NULL,
    [IsActive] bit NOT NULL,
    [LastLoginAt] datetime2 NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_PlatformUsers] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_PlatformUsers_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [ServiceCategories] (
    [Id] uniqueidentifier NOT NULL,
    [TenantId] uniqueidentifier NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [Description] nvarchar(max) NULL,
    [DisplayOrder] int NOT NULL,
    [IsActive] bit NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_ServiceCategories] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ServiceCategories_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [Subscriptions] (
    [Id] uniqueidentifier NOT NULL,
    [TenantId] uniqueidentifier NOT NULL,
    [Plan] nvarchar(50) NOT NULL,
    [Status] nvarchar(50) NOT NULL,
    [TrialStartedAt] datetime2 NOT NULL,
    [TrialEndsAt] datetime2 NOT NULL,
    [CurrentPeriodStart] datetime2 NULL,
    [CurrentPeriodEnd] datetime2 NULL,
    [StripeCustomerId] nvarchar(max) NULL,
    [StripeSubscriptionId] nvarchar(max) NULL,
    [CancelledAt] datetime2 NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_Subscriptions] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Subscriptions_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [TenantSettings] (
    [Id] uniqueidentifier NOT NULL,
    [TenantId] uniqueidentifier NOT NULL,
    [CompanyName] nvarchar(200) NOT NULL,
    [Tagline] nvarchar(max) NULL,
    [LogoUrl] nvarchar(max) NULL,
    [PrimaryColor] nvarchar(20) NOT NULL,
    [SecondaryColor] nvarchar(20) NOT NULL,
    [AccentColor] nvarchar(20) NOT NULL,
    [Phone] nvarchar(max) NULL,
    [Email] nvarchar(max) NULL,
    [Website] nvarchar(max) NULL,
    [Address] nvarchar(max) NULL,
    [BookingIntervalMinutes] int NOT NULL,
    [MaxAdvanceBookingDays] int NOT NULL,
    [TimeZone] nvarchar(100) NOT NULL DEFAULT N'Europe/Berlin',
    [DefaultCurrency] nvarchar(3) NOT NULL DEFAULT N'EUR',
    [WelcomeMessage] nvarchar(max) NULL,
    [CancellationPolicy] nvarchar(max) NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_TenantSettings] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_TenantSettings_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [BlockedTimeSlots] (
    [Id] uniqueidentifier NOT NULL,
    [TenantId] uniqueidentifier NOT NULL,
    [BlockDate] date NOT NULL,
    [StartTime] time NOT NULL,
    [EndTime] time NOT NULL,
    [Reason] nvarchar(255) NULL,
    [CreatedAt] datetime2 NOT NULL,
    [EmployeeId] uniqueidentifier NULL,
    CONSTRAINT [PK_BlockedTimeSlots] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_BlockedTimeSlots_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [Employees] ([Id]),
    CONSTRAINT [FK_BlockedTimeSlots_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [Customers] (
    [Id] uniqueidentifier NOT NULL,
    [TenantId] uniqueidentifier NOT NULL,
    [FirstName] nvarchar(100) NOT NULL,
    [LastName] nvarchar(100) NOT NULL,
    [Email] nvarchar(255) NULL,
    [Phone] nvarchar(50) NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    [LastVisit] datetime2 NULL,
    [TotalBookings] int NOT NULL,
    [NoShowCount] int NOT NULL,
    [Notes] nvarchar(max) NULL,
    [EmployeeId] uniqueidentifier NULL,
    CONSTRAINT [PK_Customers] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Customers_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [Employees] ([Id]),
    CONSTRAINT [FK_Customers_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [Services] (
    [Id] uniqueidentifier NOT NULL,
    [TenantId] uniqueidentifier NOT NULL,
    [CategoryId] uniqueidentifier NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [Description] nvarchar(max) NULL,
    [DurationMinutes] int NOT NULL,
    [Price] decimal(10,2) NOT NULL,
    [Currency] nvarchar(3) NOT NULL DEFAULT N'EUR',
    [IsActive] bit NOT NULL,
    [DisplayOrder] int NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_Services] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Services_ServiceCategories_CategoryId] FOREIGN KEY ([CategoryId]) REFERENCES [ServiceCategories] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Services_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [Bookings] (
    [Id] uniqueidentifier NOT NULL,
    [TenantId] uniqueidentifier NOT NULL,
    [CustomerId] uniqueidentifier NOT NULL,
    [ServiceId] uniqueidentifier NOT NULL,
    [EmployeeId] uniqueidentifier NULL,
    [BookingDate] date NOT NULL,
    [StartTime] time NOT NULL,
    [EndTime] time NOT NULL,
    [Status] nvarchar(50) NOT NULL,
    [ConfirmationSentAt] datetime2 NULL,
    [ReminderSentAt] datetime2 NULL,
    [CustomerNotes] nvarchar(max) NULL,
    [AdminNotes] nvarchar(max) NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    [CancelledAt] datetime2 NULL,
    [CancellationReason] nvarchar(max) NULL,
    CONSTRAINT [PK_Bookings] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Bookings_Customers_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [Customers] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Bookings_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [Employees] ([Id]),
    CONSTRAINT [FK_Bookings_Services_ServiceId] FOREIGN KEY ([ServiceId]) REFERENCES [Services] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Bookings_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [ServiceEmployees] (
    [ServiceId] uniqueidentifier NOT NULL,
    [EmployeeId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] uniqueidentifier NULL,
    CONSTRAINT [PK_ServiceEmployees] PRIMARY KEY ([ServiceId], [EmployeeId]),
    CONSTRAINT [FK_ServiceEmployees_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [Employees] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_ServiceEmployees_Services_ServiceId] FOREIGN KEY ([ServiceId]) REFERENCES [Services] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [EmailLogs] (
    [Id] uniqueidentifier NOT NULL,
    [TenantId] uniqueidentifier NOT NULL,
    [BookingId] uniqueidentifier NULL,
    [EmailType] nvarchar(50) NOT NULL,
    [RecipientEmail] nvarchar(255) NOT NULL,
    [Subject] nvarchar(500) NOT NULL,
    [Status] nvarchar(50) NOT NULL,
    [SentAt] datetime2 NULL,
    [ErrorMessage] nvarchar(max) NULL,
    [CreatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_EmailLogs] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_EmailLogs_Bookings_BookingId] FOREIGN KEY ([BookingId]) REFERENCES [Bookings] ([Id]) ON DELETE SET NULL,
    CONSTRAINT [FK_EmailLogs_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
);

CREATE INDEX [IX_BlockedTimeSlots_EmployeeId] ON [BlockedTimeSlots] ([EmployeeId]);

CREATE INDEX [IX_BlockedTimeSlots_TenantId_BlockDate] ON [BlockedTimeSlots] ([TenantId], [BlockDate]);

CREATE INDEX [IX_Bookings_CustomerId] ON [Bookings] ([CustomerId]);

CREATE INDEX [IX_Bookings_EmployeeId] ON [Bookings] ([EmployeeId]);

CREATE INDEX [IX_Bookings_ServiceId] ON [Bookings] ([ServiceId]);

CREATE INDEX [IX_Bookings_TenantId_BookingDate] ON [Bookings] ([TenantId], [BookingDate]);

CREATE INDEX [IX_Bookings_TenantId_Status] ON [Bookings] ([TenantId], [Status]);

CREATE UNIQUE INDEX [IX_BusinessHours_TenantId_DayOfWeek] ON [BusinessHours] ([TenantId], [DayOfWeek]);

CREATE INDEX [IX_Customers_EmployeeId] ON [Customers] ([EmployeeId]);

CREATE INDEX [IX_Customers_TenantId_Email] ON [Customers] ([TenantId], [Email]) WHERE [Email] IS NOT NULL AND [Email] <> '';

CREATE INDEX [IX_EmailLogs_BookingId] ON [EmailLogs] ([BookingId]);

CREATE INDEX [IX_EmailLogs_TenantId] ON [EmailLogs] ([TenantId]);

CREATE INDEX [IX_Employees_TenantId_IsActive] ON [Employees] ([TenantId], [IsActive]);

CREATE INDEX [IX_LinkClicks_ClickedAt] ON [LinkClicks] ([ClickedAt]);

CREATE INDEX [IX_PageViews_SessionId] ON [PageViews] ([SessionId]);

CREATE INDEX [IX_PageViews_ViewedAt] ON [PageViews] ([ViewedAt]);

CREATE UNIQUE INDEX [IX_PlatformUsers_Email] ON [PlatformUsers] ([Email]);

CREATE INDEX [IX_PlatformUsers_TenantId] ON [PlatformUsers] ([TenantId]);

CREATE INDEX [IX_ServiceCategories_TenantId] ON [ServiceCategories] ([TenantId]);

CREATE INDEX [IX_ServiceEmployees_EmployeeId] ON [ServiceEmployees] ([EmployeeId]);

CREATE INDEX [IX_Services_CategoryId] ON [Services] ([CategoryId]);

CREATE INDEX [IX_Services_TenantId_IsActive] ON [Services] ([TenantId], [IsActive]);

CREATE UNIQUE INDEX [IX_Subscriptions_TenantId] ON [Subscriptions] ([TenantId]);

CREATE UNIQUE INDEX [IX_Tenants_Slug] ON [Tenants] ([Slug]);

CREATE UNIQUE INDEX [IX_TenantSettings_TenantId] ON [TenantSettings] ([TenantId]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260407221111_InitialGentleBookSchema', N'8.0.11');

