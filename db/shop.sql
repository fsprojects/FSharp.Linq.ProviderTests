-- The tables of the SQLProvider tests. SQLProvider reads the names of the tables without quoting them, so they
-- cannot be named Order, a keyword of SQL, the way EF Core names the table of the class. scripts/create-db.fsx runs
-- this file and fills the tables with the data of the scenarios.

CREATE TABLE Customers (
    Id INTEGER NOT NULL CONSTRAINT PK_Customers PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    City TEXT NOT NULL,
    Age INTEGER NOT NULL,
    Registered DATETIME NULL
);

CREATE TABLE Products (
    Id INTEGER NOT NULL CONSTRAINT PK_Products PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    Category TEXT NOT NULL
);

CREATE TABLE Orders (
    Id INTEGER NOT NULL CONSTRAINT PK_Orders PRIMARY KEY AUTOINCREMENT,
    CustomerId INTEGER NOT NULL,
    Total REAL NOT NULL,
    CONSTRAINT FK_Orders_Customers_CustomerId FOREIGN KEY (CustomerId) REFERENCES Customers (Id) ON DELETE CASCADE
);

CREATE TABLE OrderLines (
    Id INTEGER NOT NULL CONSTRAINT PK_OrderLines PRIMARY KEY AUTOINCREMENT,
    OrderId INTEGER NOT NULL,
    ProductId INTEGER NOT NULL,
    Quantity INTEGER NOT NULL,
    Price REAL NOT NULL,
    Discount INTEGER NULL,
    CONSTRAINT FK_OrderLines_Orders_OrderId FOREIGN KEY (OrderId) REFERENCES Orders (Id) ON DELETE CASCADE,
    CONSTRAINT FK_OrderLines_Products_ProductId FOREIGN KEY (ProductId) REFERENCES Products (Id) ON DELETE CASCADE
);

CREATE INDEX IX_Orders_CustomerId ON Orders (CustomerId);
CREATE INDEX IX_OrderLines_OrderId ON OrderLines (OrderId);
CREATE INDEX IX_OrderLines_ProductId ON OrderLines (ProductId);
