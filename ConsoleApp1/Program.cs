using Azure;
using Azure.AI.Vision.ImageAnalysis;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

// ======================================================
// KONFIGURATION
// ======================================================

// Lokaler Bildordner auf deinem Mac.
// Optional kannst du ihn mit INPUT_FOLDER überschreiben.
string inputFolder =
    Environment.GetEnvironmentVariable("INPUT_FOLDER")
    ?? "/workspaces/test/ConsoleApp1";

string? endpoint =
    Environment.GetEnvironmentVariable("VISION_ENDPOINT");

string? key =
    Environment.GetEnvironmentVariable("VISION_KEY");

// Variante 1: komplette SQL-Verbindungszeichenfolge in SQL_CONNECTION_STRING.
// Variante 2: nur SQL_DATABASE setzen. Dann wird für deinen Azure-SQL-Server
// fghuztgh.database.windows.net "Active Directory Default" verwendet.
// Lokal funktioniert das z. B. nach `az login`, sofern dein Entra-Benutzer DB-Zugriff hat.
string? sqlConnectionString =
    Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING")
    ?? Environment.GetEnvironmentVariable("AZURE_SQL_CONNECTIONSTRING");

if (string.IsNullOrWhiteSpace(sqlConnectionString))
{
    string sqlServer =
        Environment.GetEnvironmentVariable("SQL_SERVER")
        ?? "fghuztgh.database.windows.net";

    string? sqlDatabase =
        Environment.GetEnvironmentVariable("SQL_DATABASE");

    if (!string.IsNullOrWhiteSpace(sqlDatabase))
    {
        sqlConnectionString =
            $"Server=tcp:{sqlServer},1433;" +
            $"Initial Catalog={sqlDatabase};" +
            "Encrypt=True;" +
            "TrustServerCertificate=False;" +
            "Connection Timeout=30;" +
            "Authentication=Active Directory Default;";
    }
}

// ======================================================
// KONFIGURATION PRÜFEN
// ======================================================

if (string.IsNullOrWhiteSpace(endpoint) ||
    string.IsNullOrWhiteSpace(key))
{
    Console.WriteLine("FEHLER: VISION_ENDPOINT oder VISION_KEY fehlt.");
    Console.WriteLine();
    Console.WriteLine("Bitte vorher im Terminal setzen:");
    Console.WriteLine("export VISION_ENDPOINT=\"https://DEINE-RESOURCE.cognitiveservices.azure.com/\"");
    Console.WriteLine("export VISION_KEY=\"DEIN_KEY\"");
    return;
}

if (string.IsNullOrWhiteSpace(sqlConnectionString))
{
    Console.WriteLine("FEHLER: SQL-Konfiguration fehlt.");
    Console.WriteLine();
    Console.WriteLine("Variante A (Microsoft Entra / Azure CLI):");
    Console.WriteLine("az login");
    Console.WriteLine("export SQL_DATABASE=\"DEIN_DATENBANKNAME\"");
    Console.WriteLine();
    Console.WriteLine("Optional bei anderem Server:");
    Console.WriteLine("export SQL_SERVER=\"SERVER.database.windows.net\"");
    Console.WriteLine();
    Console.WriteLine("Alternativ kann SQL_CONNECTION_STRING komplett gesetzt werden.");
    return;
}

if (!Directory.Exists(inputFolder))
{
    Console.WriteLine($"FEHLER: Bildordner existiert nicht: {inputFolder}");
    return;
}

// ======================================================
// AZURE VISION CLIENT
// ======================================================

ImageAnalysisClient visionClient =
    new ImageAnalysisClient(
        new Uri(endpoint),
        new AzureKeyCredential(key));

// ======================================================
// SQL VERBINDEN + TABELLEN AUTOMATISCH ANLEGEN
// ======================================================

using SqlConnection sqlConnection =
    new SqlConnection(sqlConnectionString);

try
{
    sqlConnection.Open();
    Console.WriteLine($"SQL verbunden: {sqlConnection.DataSource} / {sqlConnection.Database}");

    EnsureDatabaseSchema(sqlConnection);
    Console.WriteLine("SQL-Tabellen sind bereit.");
}
catch (SqlException ex)
{
    Console.WriteLine("FEHLER: Verbindung zu Azure SQL oder Schema-Erstellung fehlgeschlagen.");
    Console.WriteLine($"SQL-Fehlernummer: {ex.Number}");
    Console.WriteLine(ex.Message);
    Console.WriteLine();
    Console.WriteLine("Wenn du SQL_DATABASE verwendest, muss dein lokal angemeldeter Azure-Benutzer");
    Console.WriteLine("(z. B. via az login) Zugriff auf die SQL-Datenbank besitzen.");
    Console.WriteLine("Alternativ setze SQL_CONNECTION_STRING mit SQL-Benutzer/Passwort.");
    return;
}

// ======================================================
// BILDER SUCHEN
// ======================================================

List<string> imageFiles =
    Directory
        .EnumerateFiles(
            inputFolder,
            "*.*",
            SearchOption.TopDirectoryOnly)
        .Where(file =>
            file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
        .OrderBy(file => file)
        .ToList();

if (imageFiles.Count == 0)
{
    Console.WriteLine();
    Console.WriteLine("Keine JPG/JPEG-Dateien gefunden in:");
    Console.WriteLine(inputFolder);
    return;
}

Console.WriteLine();
Console.WriteLine($"Bildordner: {inputFolder}");
Console.WriteLine($"{imageFiles.Count} Zeichnung(en) gefunden.");
Console.WriteLine();

int successCount = 0;
int errorCount = 0;

// ======================================================
// ALLE ZEICHNUNGEN DURCHLAUFEN
// ======================================================

foreach (string imagePath in imageFiles)
{
    string fileName = Path.GetFileName(imagePath);

    Console.WriteLine("--------------------------------------------");
    Console.WriteLine($"Analysiere: {fileName}");

    try
    {
        string fileHash = ComputeFileHash(imagePath);

        // ==================================================
        // BILD AN AZURE SENDEN
        // ==================================================

        using FileStream stream = File.OpenRead(imagePath);

        ImageAnalysisResult analysisResult =
            visionClient.Analyze(
                BinaryData.FromStream(stream),
                VisualFeatures.Read);

        // ==================================================
        // OCR ERGEBNISSE SAMMELN
        // ==================================================

        List<OcrItem> ocrItems = new List<OcrItem>();

        foreach (DetectedTextBlock block in analysisResult.Read.Blocks)
        {
            foreach (DetectedTextLine line in block.Lines)
            {
                double confidence = 0;

                if (line.Words.Count > 0)
                {
                    confidence = line.Words.Average(word => word.Confidence);
                }

                double centerX = 0;
                double centerY = 0;

                if (line.BoundingPolygon.Count > 0)
                {
                    centerX = line.BoundingPolygon.Average(point => point.X);
                    centerY = line.BoundingPolygon.Average(point => point.Y);
                }

                ocrItems.Add(
                    new OcrItem
                    {
                        Text = line.Text,
                        Confidence = confidence,
                        CenterX = centerX,
                        CenterY = centerY
                    });
            }
        }

        // ==================================================
        // TECHNISCHE MERKMALE ERKENNEN
        // ==================================================

        List<DrawingFeature> features =
            ExtractDrawingFeatures(
                ocrItems,
                analysisResult.Metadata.Width,
                analysisResult.Metadata.Height);

        // ==================================================
        // SCHRIFTFELD AUSLESEN
        // ==================================================

        int imageWidth = analysisResult.Metadata.Width;
        int imageHeight = analysisResult.Metadata.Height;

        string drawingNumber =
            FindTitleValue(
                ocrItems,
                imageWidth,
                imageHeight,
                "zeichnungsnummer",
                "zeichnungs-nr",
                "zeichn.nr",
                "zeichnung nr",
                "dwg no",
                "drawing no",
                "drawing number");

        string articleNumber =
            FindTitleValue(
                ocrItems,
                imageWidth,
                imageHeight,
                "artikelnummer",
                "artikel-nr",
                "artikel nr",
                "part no",
                "part number");

        string partName =
            FindTitleValue(
                ocrItems,
                imageWidth,
                imageHeight,
                "benennung",
                "bezeichnung",
                "bauteil",
                "title",
                "description",
                "part name");

        string customer =
            FindTitleValue(
                ocrItems,
                imageWidth,
                imageHeight,
                "kunde",
                "customer",
                "client");

        string material =
            FindTitleValue(
                ocrItems,
                imageWidth,
                imageHeight,
                "werkstoff",
                "material");

        string materialThickness =
            FindTitleValue(
                ocrItems,
                imageWidth,
                imageHeight,
                "materialdicke",
                "material dicke",
                "blechdicke",
                "thickness");

        string surface =
            FindTitleValue(
                ocrItems,
                imageWidth,
                imageHeight,
                "oberfläche",
                "oberflaeche",
                "surface finish",
                "surface");

        string scale =
            FindTitleValue(
                ocrItems,
                imageWidth,
                imageHeight,
                "maßstab",
                "massstab",
                "scale");

        string drawingDate =
            FindTitleValue(
                ocrItems,
                imageWidth,
                imageHeight,
                "datum",
                "date");

        string weight =
            FindTitleValue(
                ocrItems,
                imageWidth,
                imageHeight,
                "gewicht",
                "weight");

        // ==================================================
        // MERKMALE NACH TYP ZUSAMMENFASSEN
        // ==================================================

        string lengths = JoinFeatures(features, "Längenmaß");
        string diameters = JoinFeatures(features, "Durchmesser");
        string radii = JoinFeatures(features, "Radius");
        string chamfers = JoinFeatures(features, "Fase");
        string fits = JoinFeatures(features, "Passung");
        string threads = JoinFeatures(features, "Gewinde");
        string depths = JoinFeatures(features, "Tiefe");
        string angles = JoinFeatures(features, "Winkel");

        double averageConfidence =
            ocrItems.Count > 0
                ? ocrItems.Average(item => item.Confidence)
                : 0;

        int reviewCount =
            features.Count(feature => feature.NeedsReview);

        string completeText =
            string.Join(" | ", ocrItems.Select(item => item.Text));

        string status =
            reviewCount > 0
                ? "OK - Prüfung erforderlich"
                : "OK";

        // ==================================================
        // IN SQL SPEICHERN
        // ==================================================

        DrawingRecord drawing =
            new DrawingRecord
            {
                FileHash = fileHash,
                FileName = fileName,
                DrawingNumber = drawingNumber,
                ArticleNumber = articleNumber,
                PartName = partName,
                Customer = customer,
                Material = material,
                MaterialThickness = materialThickness,
                Surface = surface,
                Scale = scale,
                DrawingDate = drawingDate,
                Weight = weight,
                Lengths = lengths,
                Diameters = diameters,
                Radii = radii,
                Chamfers = chamfers,
                Fits = fits,
                Threads = threads,
                Depths = depths,
                Angles = angles,
                OcrConfidence = averageConfidence,
                FeatureCount = features.Count,
                ReviewCount = reviewCount,
                CompleteText = completeText,
                Status = status
            };

        (long drawingId, bool updated) =
            SaveDrawingToSql(
                sqlConnection,
                drawing,
                features);

        Console.WriteLine($"OCR-Zeilen: {ocrItems.Count}");
        Console.WriteLine($"Merkmale erkannt: {features.Count}");
        Console.WriteLine($"Davon prüfen: {reviewCount}");
        Console.WriteLine($"SQL DrawingId: {drawingId}");
        Console.WriteLine(updated ? "SQL: vorhandener Datensatz aktualisiert" : "SQL: neuer Datensatz angelegt");
        Console.WriteLine("Status: OK");

        successCount++;
    }
    catch (RequestFailedException ex)
    {
        errorCount++;
        Console.WriteLine($"AZURE-FEHLER bei {fileName}");
        Console.WriteLine($"Status: {ex.Status}");
        Console.WriteLine(ex.Message);
    }
    catch (SqlException ex)
    {
        errorCount++;
        Console.WriteLine($"SQL-FEHLER bei {fileName}");
        Console.WriteLine($"SQL-Fehlernummer: {ex.Number}");
        Console.WriteLine(ex.Message);
    }
    catch (Exception ex)
    {
        errorCount++;
        Console.WriteLine($"FEHLER bei {fileName}:");
        Console.WriteLine(ex.Message);
    }
}

Console.WriteLine();
Console.WriteLine("============================================");
Console.WriteLine("AUSWERTUNG ABGESCHLOSSEN");
Console.WriteLine("============================================");
Console.WriteLine($"Zeichnungen gesamt: {imageFiles.Count}");
Console.WriteLine($"Erfolgreich: {successCount}");
Console.WriteLine($"Fehler: {errorCount}");
Console.WriteLine($"SQL-Datenbank: {sqlConnection.Database}");

// ======================================================
// SQL-SCHEMA AUTOMATISCH ANLEGEN
// ======================================================

static void EnsureDatabaseSchema(SqlConnection connection)
{
    const string sql = """
        IF OBJECT_ID(N'dbo.Drawings', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.Drawings
            (
                Id BIGINT IDENTITY(1,1) NOT NULL
                    CONSTRAINT PK_Drawings PRIMARY KEY,

                FileHash CHAR(64) NOT NULL,
                FileName NVARCHAR(260) NOT NULL,

                DrawingNumber NVARCHAR(255) NULL,
                ArticleNumber NVARCHAR(255) NULL,
                PartName NVARCHAR(255) NULL,
                Customer NVARCHAR(255) NULL,
                Material NVARCHAR(255) NULL,
                MaterialThickness NVARCHAR(255) NULL,
                Surface NVARCHAR(255) NULL,
                Scale NVARCHAR(100) NULL,
                DrawingDate NVARCHAR(100) NULL,
                Weight NVARCHAR(100) NULL,

                Lengths NVARCHAR(MAX) NULL,
                Diameters NVARCHAR(MAX) NULL,
                Radii NVARCHAR(MAX) NULL,
                Chamfers NVARCHAR(MAX) NULL,
                Fits NVARCHAR(MAX) NULL,
                Threads NVARCHAR(MAX) NULL,
                Depths NVARCHAR(MAX) NULL,
                Angles NVARCHAR(MAX) NULL,

                OcrConfidence FLOAT NULL,
                FeatureCount INT NOT NULL,
                ReviewCount INT NOT NULL,
                CompleteText NVARCHAR(MAX) NULL,
                Status NVARCHAR(100) NULL,

                CreatedAtUtc DATETIME2(0) NOT NULL
                    CONSTRAINT DF_Drawings_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
                UpdatedAtUtc DATETIME2(0) NOT NULL
                    CONSTRAINT DF_Drawings_UpdatedAtUtc DEFAULT SYSUTCDATETIME()
            );
        END;

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.indexes
            WHERE name = N'UX_Drawings_FileHash'
              AND object_id = OBJECT_ID(N'dbo.Drawings')
        )
        BEGIN
            CREATE UNIQUE INDEX UX_Drawings_FileHash
                ON dbo.Drawings(FileHash);
        END;

        IF OBJECT_ID(N'dbo.DrawingFeatures', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.DrawingFeatures
            (
                Id BIGINT IDENTITY(1,1) NOT NULL
                    CONSTRAINT PK_DrawingFeatures PRIMARY KEY,

                DrawingId BIGINT NOT NULL,
                FeatureType NVARCHAR(100) NOT NULL,
                RawText NVARCHAR(MAX) NULL,
                Quantity INT NOT NULL
                    CONSTRAINT DF_DrawingFeatures_Quantity DEFAULT 1,
                PrimaryValue FLOAT NULL,
                SecondaryValue FLOAT NULL,
                Tolerance NVARCHAR(255) NULL,
                Unit NVARCHAR(50) NULL,
                Confidence FLOAT NULL,
                NeedsReview BIT NOT NULL,
                ReviewReason NVARCHAR(500) NULL,
                PositionX FLOAT NULL,
                PositionY FLOAT NULL,

                CONSTRAINT FK_DrawingFeatures_Drawings
                    FOREIGN KEY (DrawingId)
                    REFERENCES dbo.Drawings(Id)
                    ON DELETE CASCADE
            );
        END;

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.indexes
            WHERE name = N'IX_DrawingFeatures_DrawingId'
              AND object_id = OBJECT_ID(N'dbo.DrawingFeatures')
        )
        BEGIN
            CREATE INDEX IX_DrawingFeatures_DrawingId
                ON dbo.DrawingFeatures(DrawingId);
        END;
        """;

    using SqlCommand command = new SqlCommand(sql, connection);
    command.CommandTimeout = 60;
    command.ExecuteNonQuery();
}

// ======================================================
// ZEICHNUNG + MERKMALE IN SQL SPEICHERN
// ======================================================

static (long DrawingId, bool Updated) SaveDrawingToSql(
    SqlConnection connection,
    DrawingRecord drawing,
    List<DrawingFeature> features)
{
    using SqlTransaction transaction = connection.BeginTransaction();

    try
    {
        long? existingId = null;

        const string findSql = """
            SELECT Id
            FROM dbo.Drawings
            WHERE FileHash = @FileHash;
            """;

        using (SqlCommand findCommand =
               new SqlCommand(findSql, connection, transaction))
        {
            findCommand.Parameters
                .Add("@FileHash", SqlDbType.Char, 64)
                .Value = drawing.FileHash;

            object? result = findCommand.ExecuteScalar();

            if (result != null && result != DBNull.Value)
            {
                existingId = Convert.ToInt64(result);
            }
        }

        long drawingId;
        bool updated = existingId.HasValue;

        if (existingId.HasValue)
        {
            drawingId = existingId.Value;

            const string updateSql = """
                UPDATE dbo.Drawings
                SET
                    FileName = @FileName,
                    DrawingNumber = @DrawingNumber,
                    ArticleNumber = @ArticleNumber,
                    PartName = @PartName,
                    Customer = @Customer,
                    Material = @Material,
                    MaterialThickness = @MaterialThickness,
                    Surface = @Surface,
                    Scale = @Scale,
                    DrawingDate = @DrawingDate,
                    Weight = @Weight,
                    Lengths = @Lengths,
                    Diameters = @Diameters,
                    Radii = @Radii,
                    Chamfers = @Chamfers,
                    Fits = @Fits,
                    Threads = @Threads,
                    Depths = @Depths,
                    Angles = @Angles,
                    OcrConfidence = @OcrConfidence,
                    FeatureCount = @FeatureCount,
                    ReviewCount = @ReviewCount,
                    CompleteText = @CompleteText,
                    Status = @Status,
                    UpdatedAtUtc = SYSUTCDATETIME()
                WHERE Id = @Id;
                """;

            using SqlCommand updateCommand =
                new SqlCommand(updateSql, connection, transaction);

            AddDrawingParameters(updateCommand, drawing);
            updateCommand.Parameters.Add("@Id", SqlDbType.BigInt).Value = drawingId;
            updateCommand.ExecuteNonQuery();

            using SqlCommand deleteFeaturesCommand =
                new SqlCommand(
                    "DELETE FROM dbo.DrawingFeatures WHERE DrawingId = @DrawingId;",
                    connection,
                    transaction);

            deleteFeaturesCommand.Parameters
                .Add("@DrawingId", SqlDbType.BigInt)
                .Value = drawingId;

            deleteFeaturesCommand.ExecuteNonQuery();
        }
        else
        {
            const string insertSql = """
                INSERT INTO dbo.Drawings
                (
                    FileHash,
                    FileName,
                    DrawingNumber,
                    ArticleNumber,
                    PartName,
                    Customer,
                    Material,
                    MaterialThickness,
                    Surface,
                    Scale,
                    DrawingDate,
                    Weight,
                    Lengths,
                    Diameters,
                    Radii,
                    Chamfers,
                    Fits,
                    Threads,
                    Depths,
                    Angles,
                    OcrConfidence,
                    FeatureCount,
                    ReviewCount,
                    CompleteText,
                    Status,
                    CreatedAtUtc,
                    UpdatedAtUtc
                )
                OUTPUT INSERTED.Id
                VALUES
                (
                    @FileHash,
                    @FileName,
                    @DrawingNumber,
                    @ArticleNumber,
                    @PartName,
                    @Customer,
                    @Material,
                    @MaterialThickness,
                    @Surface,
                    @Scale,
                    @DrawingDate,
                    @Weight,
                    @Lengths,
                    @Diameters,
                    @Radii,
                    @Chamfers,
                    @Fits,
                    @Threads,
                    @Depths,
                    @Angles,
                    @OcrConfidence,
                    @FeatureCount,
                    @ReviewCount,
                    @CompleteText,
                    @Status,
                    SYSUTCDATETIME(),
                    SYSUTCDATETIME()
                );
                """;

            using SqlCommand insertCommand =
                new SqlCommand(insertSql, connection, transaction);

            insertCommand.Parameters
                .Add("@FileHash", SqlDbType.Char, 64)
                .Value = drawing.FileHash;

            AddDrawingParameters(insertCommand, drawing);

            object? result = insertCommand.ExecuteScalar();

            if (result == null || result == DBNull.Value)
            {
                throw new InvalidOperationException(
                    "Die neue DrawingId konnte nicht aus SQL gelesen werden.");
            }

            drawingId = Convert.ToInt64(result);
        }

        InsertFeatures(
            connection,
            transaction,
            drawingId,
            features);

        transaction.Commit();
        return (drawingId, updated);
    }
    catch
    {
        transaction.Rollback();
        throw;
    }
}

static void AddDrawingParameters(
    SqlCommand command,
    DrawingRecord drawing)
{
    AddRequiredNVarChar(command, "@FileName", 260, drawing.FileName);
    AddNullableNVarChar(command, "@DrawingNumber", 255, drawing.DrawingNumber);
    AddNullableNVarChar(command, "@ArticleNumber", 255, drawing.ArticleNumber);
    AddNullableNVarChar(command, "@PartName", 255, drawing.PartName);
    AddNullableNVarChar(command, "@Customer", 255, drawing.Customer);
    AddNullableNVarChar(command, "@Material", 255, drawing.Material);
    AddNullableNVarChar(command, "@MaterialThickness", 255, drawing.MaterialThickness);
    AddNullableNVarChar(command, "@Surface", 255, drawing.Surface);
    AddNullableNVarChar(command, "@Scale", 100, drawing.Scale);
    AddNullableNVarChar(command, "@DrawingDate", 100, drawing.DrawingDate);
    AddNullableNVarChar(command, "@Weight", 100, drawing.Weight);

    AddNullableNVarCharMax(command, "@Lengths", drawing.Lengths);
    AddNullableNVarCharMax(command, "@Diameters", drawing.Diameters);
    AddNullableNVarCharMax(command, "@Radii", drawing.Radii);
    AddNullableNVarCharMax(command, "@Chamfers", drawing.Chamfers);
    AddNullableNVarCharMax(command, "@Fits", drawing.Fits);
    AddNullableNVarCharMax(command, "@Threads", drawing.Threads);
    AddNullableNVarCharMax(command, "@Depths", drawing.Depths);
    AddNullableNVarCharMax(command, "@Angles", drawing.Angles);

    command.Parameters.Add("@OcrConfidence", SqlDbType.Float).Value = drawing.OcrConfidence;
    command.Parameters.Add("@FeatureCount", SqlDbType.Int).Value = drawing.FeatureCount;
    command.Parameters.Add("@ReviewCount", SqlDbType.Int).Value = drawing.ReviewCount;

    AddNullableNVarCharMax(command, "@CompleteText", drawing.CompleteText);
    AddNullableNVarChar(command, "@Status", 100, drawing.Status);
}

static void InsertFeatures(
    SqlConnection connection,
    SqlTransaction transaction,
    long drawingId,
    List<DrawingFeature> features)
{
    if (features.Count == 0)
    {
        return;
    }

    const string insertFeatureSql = """
        INSERT INTO dbo.DrawingFeatures
        (
            DrawingId,
            FeatureType,
            RawText,
            Quantity,
            PrimaryValue,
            SecondaryValue,
            Tolerance,
            Unit,
            Confidence,
            NeedsReview,
            ReviewReason,
            PositionX,
            PositionY
        )
        VALUES
        (
            @DrawingId,
            @FeatureType,
            @RawText,
            @Quantity,
            @PrimaryValue,
            @SecondaryValue,
            @Tolerance,
            @Unit,
            @Confidence,
            @NeedsReview,
            @ReviewReason,
            @PositionX,
            @PositionY
        );
        """;

    using SqlCommand command =
        new SqlCommand(insertFeatureSql, connection, transaction);

    SqlParameter drawingIdParameter =
        command.Parameters.Add("@DrawingId", SqlDbType.BigInt);

    SqlParameter featureTypeParameter =
        command.Parameters.Add("@FeatureType", SqlDbType.NVarChar, 100);

    SqlParameter rawTextParameter =
        command.Parameters.Add("@RawText", SqlDbType.NVarChar, -1);

    SqlParameter quantityParameter =
        command.Parameters.Add("@Quantity", SqlDbType.Int);

    SqlParameter primaryValueParameter =
        command.Parameters.Add("@PrimaryValue", SqlDbType.Float);

    SqlParameter secondaryValueParameter =
        command.Parameters.Add("@SecondaryValue", SqlDbType.Float);

    SqlParameter toleranceParameter =
        command.Parameters.Add("@Tolerance", SqlDbType.NVarChar, 255);

    SqlParameter unitParameter =
        command.Parameters.Add("@Unit", SqlDbType.NVarChar, 50);

    SqlParameter confidenceParameter =
        command.Parameters.Add("@Confidence", SqlDbType.Float);

    SqlParameter needsReviewParameter =
        command.Parameters.Add("@NeedsReview", SqlDbType.Bit);

    SqlParameter reviewReasonParameter =
        command.Parameters.Add("@ReviewReason", SqlDbType.NVarChar, 500);

    SqlParameter positionXParameter =
        command.Parameters.Add("@PositionX", SqlDbType.Float);

    SqlParameter positionYParameter =
        command.Parameters.Add("@PositionY", SqlDbType.Float);

    foreach (DrawingFeature feature in features)
    {
        drawingIdParameter.Value = drawingId;
        featureTypeParameter.Value = feature.Type;
        rawTextParameter.Value = DbString(feature.RawText);
        quantityParameter.Value = feature.Quantity;
        primaryValueParameter.Value =
            feature.PrimaryValue.HasValue
                ? (object)feature.PrimaryValue.Value
                : DBNull.Value;
        secondaryValueParameter.Value =
            feature.SecondaryValue.HasValue
                ? (object)feature.SecondaryValue.Value
                : DBNull.Value;
        toleranceParameter.Value = DbString(feature.Tolerance);
        unitParameter.Value = DbString(feature.Unit);
        confidenceParameter.Value = feature.Confidence;
        needsReviewParameter.Value = feature.NeedsReview;
        reviewReasonParameter.Value = DbString(feature.ReviewReason);
        positionXParameter.Value = feature.X;
        positionYParameter.Value = feature.Y;

        command.ExecuteNonQuery();
    }
}

static void AddRequiredNVarChar(
    SqlCommand command,
    string parameterName,
    int size,
    string value)
{
    command.Parameters
        .Add(parameterName, SqlDbType.NVarChar, size)
        .Value = value;
}

static void AddNullableNVarChar(
    SqlCommand command,
    string parameterName,
    int size,
    string? value)
{
    command.Parameters
        .Add(parameterName, SqlDbType.NVarChar, size)
        .Value = DbString(value);
}

static void AddNullableNVarCharMax(
    SqlCommand command,
    string parameterName,
    string? value)
{
    command.Parameters
        .Add(parameterName, SqlDbType.NVarChar, -1)
        .Value = DbString(value);
}

static object DbString(string? value)
{
    return string.IsNullOrWhiteSpace(value)
        ? DBNull.Value
        : value;
}

// ======================================================
// DATEI-HASH FÜR DUPLIKATERKENNUNG
// ======================================================

static string ComputeFileHash(string filePath)
{
    using SHA256 sha256 = SHA256.Create();
    using FileStream stream = File.OpenRead(filePath);

    byte[] hash = sha256.ComputeHash(stream);
    return Convert.ToHexString(hash);
}

// ======================================================

// TECHNISCHE MERKMALE ERKENNEN

// ======================================================


static List<DrawingFeature> ExtractDrawingFeatures(

    List<OcrItem> items,

    int imageWidth,

    int imageHeight)

{

    List<DrawingFeature> features =

        new List<DrawingFeature>();


    foreach (OcrItem item in items)

    {

        string text =

            NormalizeText(item.Text);


        if (string.IsNullOrWhiteSpace(text))

        {

            continue;

        }


        // =================================================

        // ZEICHNUNGSRAHMEN IGNORIEREN

        // =================================================


        if (IsFrameZoneLabel(

            item,

            text,

            imageWidth,

            imageHeight))

        {

            continue;

        }


        // =================================================

        // SCHRIFTFELD NICHT ALS GEOMETRISCHE MASSE WERTEN

        // =================================================


        if (IsTitleBlockRegion(

            item,

            imageWidth,

            imageHeight))

        {

            continue;

        }


        bool lowConfidence =

            item.Confidence < 0.80;


        // =================================================

        // FASE

        //

        // 0,5 X 45°

        // 1 X 45°

        // =================================================


        Match chamfer =

            Regex.Match(

                text,

                @"^\s*(\d+(?:[.,]\d+)?)\s*[xX]\s*(\d+(?:[.,]\d+)?)\s*°\s*$"

            );


        if (chamfer.Success)

        {

            features.Add(

                new DrawingFeature

                {

                    Type = "Fase",


                    RawText =

                        item.Text,


                    PrimaryValue =

                        ParseNumber(

                            chamfer

                                .Groups[1]

                                .Value),


                    SecondaryValue =

                        ParseNumber(

                            chamfer

                                .Groups[2]

                                .Value),


                    Unit =

                        "mm / Grad",


                    Confidence =

                        item.Confidence,


                    X =

                        item.CenterX,


                    Y =

                        item.CenterY,


                    NeedsReview =

                        lowConfidence,


                    ReviewReason =

                        lowConfidence

                            ? "OCR-Konfidenz unter 80 %"

                            : ""

                }

            );


            continue;

        }


        // =================================================

        // GEWINDE

        //

        // M6

        // 2xM6

        // M10x1,5

        // =================================================


        Match thread =

            Regex.Match(

                text,

                @"^\s*(?:(\d+)\s*[xX]\s*)?M\s*(\d+(?:[.,]\d+)?)(?:\s*[xX]\s*(\d+(?:[.,]\d+)?))?\s*$",

                RegexOptions.IgnoreCase

            );


        if (thread.Success)

        {

            int quantity =

                GetQuantity(

                    thread.Groups[1]);


            features.Add(

                new DrawingFeature

                {

                    Type =

                        "Gewinde",


                    RawText =

                        item.Text,


                    Quantity =

                        quantity,


                    PrimaryValue =

                        ParseNumber(

                            thread

                                .Groups[2]

                                .Value),


                    SecondaryValue =

                        thread

                            .Groups[3]

                            .Success

                            ? ParseNumber(

                                thread

                                    .Groups[3]

                                    .Value)

                            : null,


                    Unit =

                        "mm",


                    Confidence =

                        item.Confidence,


                    X =

                        item.CenterX,


                    Y =

                        item.CenterY,


                    NeedsReview =

                        lowConfidence,


                    ReviewReason =

                        lowConfidence

                            ? "OCR-Konfidenz unter 80 %"

                            : ""

                }

            );


            continue;

        }


        // =================================================

        // DURCHMESSER

        //

        // Ø4

        // ⌀4

        // O4

        // 2xØ5

        // =================================================


        Match diameter =

            Regex.Match(

                text,

                @"^\s*(?:(\d+)\s*[xX]\s*)?[Ø⌀Oo]\s*(\d+(?:[.,]\d+)?)\s*$"

            );


        if (diameter.Success)

        {

            int quantity =

                GetQuantity(

                    diameter.Groups[1]);


            features.Add(

                new DrawingFeature

                {

                    Type =

                        "Durchmesser",


                    RawText =

                        item.Text,


                    Quantity =

                        quantity,


                    PrimaryValue =

                        ParseNumber(

                            diameter

                                .Groups[2]

                                .Value),


                    Unit =

                        "mm",


                    Confidence =

                        item.Confidence,


                    X =

                        item.CenterX,


                    Y =

                        item.CenterY,


                    NeedsReview =

                        lowConfidence,


                    ReviewReason =

                        lowConfidence

                            ? "OCR-Konfidenz unter 80 %"

                            : ""

                }

            );


            continue;

        }


        // =================================================

        // OCR-FEHLER:

        //

        // Ø4 -> 04

        // Ø5 -> 05

        //

        // Wird als Durchmesser vermutet,

        // aber bewusst zur Prüfung markiert.

        // =================================================


        Match missingDiameterSymbol =

            Regex.Match(

                text,

                @"^0([1-9]\d?)$"

            );


        if (missingDiameterSymbol.Success)

        {

            features.Add(

                new DrawingFeature

                {

                    Type =

                        "Durchmesser",


                    RawText =

                        item.Text,


                    PrimaryValue =

                        ParseNumber(

                            missingDiameterSymbol

                                .Groups[1]

                                .Value),


                    Unit =

                        "mm",


                    Confidence =

                        item.Confidence,


                    X =

                        item.CenterX,


                    Y =

                        item.CenterY,


                    NeedsReview =

                        true,


                    ReviewReason =

                        "Vermutlich verlorenes Ø-Zeichen"

                }

            );


            continue;

        }


        // =================================================

        // RADIUS

        //

        // R5

        // R5.00

        // R0,5

        // =================================================


        Match radius =

            Regex.Match(

                text,

                @"^\s*R\s*(\d+(?:[.,]\d+)?)\s*$",

                RegexOptions.IgnoreCase

            );


        if (radius.Success)

        {

            features.Add(

                new DrawingFeature

                {

                    Type =

                        "Radius",


                    RawText =

                        item.Text,


                    PrimaryValue =

                        ParseNumber(

                            radius

                                .Groups[1]

                                .Value),


                    Unit =

                        "mm",


                    Confidence =

                        item.Confidence,


                    X =

                        item.CenterX,


                    Y =

                        item.CenterY,


                    NeedsReview =

                        lowConfidence,


                    ReviewReason =

                        lowConfidence

                            ? "OCR-Konfidenz unter 80 %"

                            : ""

                }

            );


            continue;

        }


        // =================================================

        // PASSUNG

        //

        // 10h7

        // 20H7

        // =================================================


        Match fit =

            Regex.Match(

                text,

                @"^\s*(\d+(?:[.,]\d+)?)\s*([A-Za-z]{1,2}\d{1,2})\s*$"

            );


        if (fit.Success)

        {

            features.Add(

                new DrawingFeature

                {

                    Type =

                        "Passung",


                    RawText =

                        item.Text,


                    PrimaryValue =

                        ParseNumber(

                            fit

                                .Groups[1]

                                .Value),


                    Tolerance =

                        fit

                            .Groups[2]

                            .Value,


                    Unit =

                        "mm",


                    Confidence =

                        item.Confidence,


                    X =

                        item.CenterX,


                    Y =

                        item.CenterY,


                    NeedsReview =

                        lowConfidence,


                    ReviewReason =

                        lowConfidence

                            ? "OCR-Konfidenz unter 80 %"

                            : ""

                }

            );


            continue;

        }


        // =================================================

        // TIEFE

        //

        // 4mm tief

        // D2-4mm tief

        // =================================================


        Match depth =

            Regex.Match(

                text,

                @"(\d+(?:[.,]\d+)?)\s*mm\s*(?:tief|deep)",

                RegexOptions.IgnoreCase

            );


        if (depth.Success)

        {

            features.Add(

                new DrawingFeature

                {

                    Type =

                        "Tiefe",


                    RawText =

                        item.Text,


                    PrimaryValue =

                        ParseNumber(

                            depth

                                .Groups[1]

                                .Value),


                    Unit =

                        "mm",


                    Confidence =

                        item.Confidence,


                    X =

                        item.CenterX,


                    Y =

                        item.CenterY,


                    NeedsReview =

                        lowConfidence,


                    ReviewReason =

                        lowConfidence

                            ? "OCR-Konfidenz unter 80 %"

                            : ""

                }

            );


            continue;

        }


        // =================================================

        // WINKEL

        //

        // 90°

        // =================================================


        Match angle =

            Regex.Match(

                text,

                @"^\s*(\d+(?:[.,]\d+)?)\s*°\s*$"

            );


        if (angle.Success)

        {

            features.Add(

                new DrawingFeature

                {

                    Type =

                        "Winkel",


                    RawText =

                        item.Text,


                    PrimaryValue =

                        ParseNumber(

                            angle

                                .Groups[1]

                                .Value),


                    Unit =

                        "Grad",


                    Confidence =

                        item.Confidence,


                    X =

                        item.CenterX,


                    Y =

                        item.CenterY,


                    NeedsReview =

                        lowConfidence,


                    ReviewReason =

                        lowConfidence

                            ? "OCR-Konfidenz unter 80 %"

                            : ""

                }

            );


            continue;

        }


        // =================================================

        // NORMALES MASS MIT OPTIONALER TOLERANZ

        //

        // 80

        // 80.00

        // 6,50 ± 0,25

        // =================================================


        Match dimension =

            Regex.Match(

                text,

                @"^\s*(\d+(?:[.,]\d+)?)(?:\s*±\s*(\d+(?:[.,]\d+)?))?\s*$"

            );


        if (dimension.Success)

        {

            string? tolerance =

                dimension.Groups[2].Success

                    ? "±" +

                      dimension

                          .Groups[2]

                          .Value

                    : null;


            features.Add(

                new DrawingFeature

                {

                    Type =

                        "Längenmaß",


                    RawText =

                        item.Text,


                    PrimaryValue =

                        ParseNumber(

                            dimension

                                .Groups[1]

                                .Value),


                    Tolerance =

                        tolerance,


                    Unit =

                        "mm",


                    Confidence =

                        item.Confidence,


                    X =

                        item.CenterX,


                    Y =

                        item.CenterY,


                    NeedsReview =

                        lowConfidence,


                    ReviewReason =

                        lowConfidence

                            ? "OCR-Konfidenz unter 80 %"

                            : ""

                }

            );

        }

    }


    return features;

}


// ======================================================

// ANZAHL AUS REGEX AUSLESEN

// ======================================================


static int GetQuantity(

    Group quantityGroup)

{

    if (!quantityGroup.Success)

    {

        return 1;

    }


    if (!int.TryParse(

        quantityGroup.Value,

        out int quantity))

    {

        return 1;

    }


    return quantity > 0

        ? quantity

        : 1;

}


// ======================================================

// ZONENNUMMERN / RAHMENBUCHSTABEN IGNORIEREN

// ======================================================


static bool IsFrameZoneLabel(

    OcrItem item,

    string text,

    int width,

    int height)

{

    // Oberer Rand:

    // 1 2 3 4 5 6 ...


    if (Regex.IsMatch(

            text,

            @"^[1-9]$")

        &&

        item.CenterY <

        height * 0.075)

    {

        return true;

    }


    // Unterer Rand ebenfalls möglich


    if (Regex.IsMatch(

            text,

            @"^[1-9]$")

        &&

        item.CenterY >

        height * 0.94)

    {

        return true;

    }


    // A, B, C, D ...

    // links/rechts am Zeichnungsrahmen


    if (Regex.IsMatch(

            text,

            @"^[A-H]$",

            RegexOptions.IgnoreCase))

    {

        if (item.CenterX <

                width * 0.05

            ||

            item.CenterX >

                width * 0.95)

        {

            return true;

        }

    }


    return false;

}


// ======================================================

// SCHRIFTFELD ERKENNEN

// ======================================================


static bool IsTitleBlockRegion(

    OcrItem item,

    int width,

    int height)

{

    // Übliche Position:

    // unten rechts


    return

        item.CenterY >

            height * 0.72

        &&

        item.CenterX >

            width * 0.35;

}


// ======================================================

// WERT IM SCHRIFTFELD SUCHEN

// ======================================================


static string FindTitleValue(

    List<OcrItem> items,

    int imageWidth,

    int imageHeight,

    params string[] labels)

{

    foreach (OcrItem item in items)

    {

        string normalizedItem =

            NormalizeForSearch(item.Text);


        foreach (string label in labels)

        {

            string normalizedLabel =

                NormalizeForSearch(label);


            int index =

                normalizedItem.IndexOf(

                    normalizedLabel,

                    StringComparison.OrdinalIgnoreCase);


            if (index < 0)

            {

                continue;

            }


            // ==========================================

            // Wert befindet sich auf derselben OCR-Zeile

            //

            // Beispiel:

            // Material: S235

            // ==========================================


            int originalIndex =

                item.Text.IndexOf(

                    label,

                    StringComparison.OrdinalIgnoreCase);


            if (originalIndex >= 0)

            {

                int start =

                    Math.Min(

                        item.Text.Length,

                        originalIndex +

                        label.Length);


                string remainder =

                    item.Text[start..]

                        .Trim(

                            ' ',

                            ':',

                            '-',

                            '=',

                            '|');


                if (!string.IsNullOrWhiteSpace(

                    remainder))

                {

                    return remainder;

                }

            }


            // ==========================================

            // NÄCHSTEN TEXT RECHTS DANEBEN SUCHEN

            // ==========================================


            double yTolerance =

                imageHeight * 0.025;


            OcrItem? rightCandidate =

                items

                    .Where(candidate =>

                        candidate != item

                        &&

                        candidate.CenterX >

                            item.CenterX

                        &&

                        Math.Abs(

                            candidate.CenterY -

                            item.CenterY)

                        <

                        yTolerance)

                    .OrderBy(candidate =>

                        candidate.CenterX -

                        item.CenterX)

                    .FirstOrDefault();


            if (rightCandidate != null)

            {

                return rightCandidate.Text;

            }


            // ==========================================

            // ALTERNATIV DIREKT DARUNTER

            // ==========================================


            OcrItem? belowCandidate =

                items

                    .Where(candidate =>

                        candidate != item

                        &&

                        candidate.CenterY >

                            item.CenterY

                        &&

                        Math.Abs(

                            candidate.CenterX -

                            item.CenterX)

                        <

                        imageWidth * 0.15)

                    .OrderBy(candidate =>

                        candidate.CenterY -

                        item.CenterY)

                    .FirstOrDefault();


            if (belowCandidate != null)

            {

                return belowCandidate.Text;

            }

        }

    }


    return "";

}


// ======================================================

// TEXT FÜR LABEL-SUCHE NORMALISIEREN

// ======================================================


static string NormalizeForSearch(

    string text)

{

    return text

        .Trim()

        .ToLowerInvariant()

        .Replace("ä", "ae")

        .Replace("ö", "oe")

        .Replace("ü", "ue")

        .Replace("ß", "ss")

        .Replace(":", "")

        .Replace(".", "")

        .Replace("-", "")

        .Replace(" ", "");

}


// ======================================================

// MERKMALE FÜR ÜBERSICHT ZUSAMMENFASSEN

// ======================================================


static string JoinFeatures(

    List<DrawingFeature> features,

    string type)

{

    return string.Join(

        "; ",

        features

            .Where(feature =>

                feature.Type == type)

            .Select(feature =>

            {

                string prefix =

                    feature.Quantity > 1

                        ? $"{feature.Quantity}x "

                        : "";


                return prefix +

                       feature.RawText;

            })

            .Distinct()

    );

}


// ======================================================

// OCR-TEXT NORMALISIEREN

// ======================================================


static string NormalizeText(

    string input)

{

    return input

        .Trim()

        .Replace("⌀", "Ø")

        .Replace("×", "X")

        .Replace("º", "°")

        .Replace("˚", "°");

}


// ======================================================

// ZAHL PARSEN

// ======================================================


static double ParseNumber(

    string value)

{

    value =

        value

            .Trim()

            .Replace(',', '.');


    return double.Parse(

        value,

        CultureInfo.InvariantCulture);

}

// ======================================================

// DATENKLASSEN

// ======================================================


public class OcrItem

{

    public string Text { get; set; } = "";


    public double Confidence { get; set; }


    public double CenterX { get; set; }


    public double CenterY { get; set; }

}


public class DrawingFeature

{

    public string Type { get; set; } = "";


    public string RawText { get; set; } = "";


    public int Quantity { get; set; } = 1;


    public double? PrimaryValue { get; set; }


    public double? SecondaryValue { get; set; }


    public string? Tolerance { get; set; }


    public string? Unit { get; set; }


    public double Confidence { get; set; }


    public bool NeedsReview { get; set; }


    public string ReviewReason { get; set; } = "";


    public double X { get; set; }


    public double Y { get; set; }

}


public class DrawingRecord
{
    public string FileHash { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? DrawingNumber { get; set; }
    public string? ArticleNumber { get; set; }
    public string? PartName { get; set; }
    public string? Customer { get; set; }
    public string? Material { get; set; }
    public string? MaterialThickness { get; set; }
    public string? Surface { get; set; }
    public string? Scale { get; set; }
    public string? DrawingDate { get; set; }
    public string? Weight { get; set; }
    public string? Lengths { get; set; }
    public string? Diameters { get; set; }
    public string? Radii { get; set; }
    public string? Chamfers { get; set; }
    public string? Fits { get; set; }
    public string? Threads { get; set; }
    public string? Depths { get; set; }
    public string? Angles { get; set; }
    public double OcrConfidence { get; set; }
    public int FeatureCount { get; set; }
    public int ReviewCount { get; set; }
    public string? CompleteText { get; set; }
    public string? Status { get; set; }
}
