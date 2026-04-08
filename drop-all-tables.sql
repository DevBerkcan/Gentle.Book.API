-- Skript zum Löschen aller Tabellen in umgekehrter Reihenfolge (wegen Foreign Key Constraints)
-- Führen Sie dieses Skript aus, bevor Sie das deploy-migration.sql erneut ausführen.

DROP TABLE IF EXISTS [EmailLogs];
DROP TABLE IF EXISTS [ServiceEmployees];
DROP TABLE IF EXISTS [Bookings];
DROP TABLE IF EXISTS [Services];
DROP TABLE IF EXISTS [Customers];
DROP TABLE IF EXISTS [BlockedTimeSlots];
DROP TABLE IF EXISTS [TenantSettings];
DROP TABLE IF EXISTS [Subscriptions];
DROP TABLE IF EXISTS [ServiceCategories];
DROP TABLE IF EXISTS [PlatformUsers];
DROP TABLE IF EXISTS [Employees];
DROP TABLE IF EXISTS [BusinessHours];
DROP TABLE IF EXISTS [Tenants];
DROP TABLE IF EXISTS [PageViews];
DROP TABLE IF EXISTS [LinkClicks];
DROP TABLE IF EXISTS [__EFMigrationsHistory];