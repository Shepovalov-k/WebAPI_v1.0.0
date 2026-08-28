using Microsoft.EntityFrameworkCore;
using TimescaleApi.Data;
using TimescaleApi.DTOs;
using TimescaleApi.Models;
using TimescaleApi.Services;
using Microsoft.OpenApi;
using System.Text.Json.Serialization;
using TimescaleApi.Middleware;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<ICsvProcessingService, CsvProcessingService>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Timescale API", Version = "v1" });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await dbContext.Database.MigrateAsync();
}

// Включение Swagger UI
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}


app.UseMiddleware<ExceptionMiddleware>();


var dataApi = app.MapGroup("/api/data");


dataApi.MapPost("/upload", async (IFormFile file, ICsvProcessingService csvService, CancellationToken ct) =>
{
    var result = await csvService.ProcessCsvAsync(file, ct);
    return Results.Ok(result);
})
.WithName("UploadCsv")
.DisableAntiforgery() 
.Produces<CsvUploadResult>(StatusCodes.Status200OK)
.ProducesProblem(StatusCodes.Status400BadRequest);


dataApi.MapGet("/results", async (
    AppDbContext db,
    string? fileName,
    DateTimeOffset? minDateFrom,
    DateTimeOffset? minDateTo,
    double? avgValueFrom,
    double? avgValueTo,
    double? avgExecutionTimeFrom,
    double? avgExecutionTimeTo,
    int pageNumber = 1,
    int pageSize = 20,
    CancellationToken ct = default) =>
{
    if (pageNumber < 1) pageNumber = 1;
    if (pageSize < 1) pageSize = 20;
    if (pageSize > 100) pageSize = 100;

    IQueryable<ResultRecord> query = db.Results
        .Include(r => r.File).AsNoTracking().AsQueryable();

    if (!string.IsNullOrWhiteSpace(fileName))
        query = query.Where(r => r.File.FileName == fileName);
    if (minDateFrom.HasValue)
        query = query.Where(r => r.MinDate >= minDateFrom.Value);
    if (minDateTo.HasValue)
        query = query.Where(r => r.MinDate <= minDateTo.Value);
    if (avgValueFrom.HasValue)
        query = query.Where(r => r.AvgValue >= avgValueFrom.Value);
    if (avgValueTo.HasValue)
        query = query.Where(r => r.AvgValue <= avgValueTo.Value);
    if (avgExecutionTimeFrom.HasValue)
        query = query.Where(r => r.AvgExecutionTime >= avgExecutionTimeFrom.Value);
    if (avgExecutionTimeTo.HasValue)
        query = query.Where(r => r.AvgExecutionTime <= avgExecutionTimeTo.Value);

    var totalCount = await query.CountAsync(ct);
    var items = await query
        .OrderByDescending(r => r.MinDate)
        .Skip((pageNumber - 1) * pageSize)
        .Take(pageSize)
        .Select(r => new ResultDto
        {
            FileName = r.File.FileName,
            DeltaTimeSeconds = r.DeltaTimeSeconds,
            MinDate = r.MinDate,
            AvgExecutionTime = r.AvgExecutionTime,
            AvgValue = r.AvgValue,
            MedianValue = r.MedianValue,
            MaxValue = r.MaxValue,
            MinValue = r.MinValue
        })
        .ToListAsync(ct);

    return Results.Ok(new PaginatedResult<ResultDto>
    {
        PageNumber = pageNumber,
        PageSize = pageSize,
        TotalCount = totalCount,
        Items = items
    });
})
.WithName("GetResults")
.Produces<PaginatedResult<ResultDto>>(StatusCodes.Status200OK);

dataApi.MapGet("/values/last", async (string fileName, AppDbContext db, CancellationToken ct) =>
{
    var fileExists = await db.Files
        .AsNoTracking()
        .AnyAsync(f => f.FileName == fileName, ct);
    if (!fileExists)
        return Results.NotFound($"Файл '{fileName}' не найден.");

    var values = await db.Values
        .AsNoTracking()
        .Where(v => v.File.FileName == fileName)
        .OrderByDescending(v => v.Date)
        .Take(10)
        .Select(v => new ValueDto
        {
            Date = v.Date,
            ExecutionTime = v.ExecutionTime,
            Value = v.Value
        })
        .ToListAsync(ct);

    return Results.Ok(values);
})
.WithName("GetLastValues")
.Produces<List<ValueDto>>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status404NotFound);

app.Run();

[JsonSerializable(typeof(CsvUploadResult))]
[JsonSerializable(typeof(ResultDto))]
[JsonSerializable(typeof(PaginatedResult<ResultDto>))]
[JsonSerializable(typeof(List<ValueDto>))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}