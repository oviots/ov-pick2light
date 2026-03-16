# Quick Reference: Batch Pick CSV Import

## New Feature: Label-Based CSV Import

The **Batch Pick** tab now supports importing CSV files using **light module labels** instead of numeric addresses. The system automatically looks up labels in the device database and maps them to addresses.

### Quick Start

**Option 1: Using Light Labels (NEW & RECOMMENDED)**
```csv
label,quantity,color
Shelf-A1,5,GREEN
Shelf-A2,3,RED
Back-Aisle-1,10,BLUE
```

**Option 2: Using Addresses (Backward Compatible)**
```csv
address,quantity,color,label
0001,5,GREEN,SKU-A001
0002,3,RED,SKU-B002
```

### How to Use

1. Set up device labels in the **Devices** tab (e.g., "Shelf-A1", "Back-Aisle-2")
2. In **Batch Pick** tab, click "⬇ Sample CSV" to download a template
3. Create your CSV using label format (easier) or address format
4. Click "Import CSV" and select your file
5. System auto-maps labels to addresses and adds items to batch

### Key Points

✅ **Labels are auto-mapped** - No need to remember numeric addresses
✅ **Case-insensitive** - "shelf-a1" matches "Shelf-A1"
✅ **Error reporting** - Unmapped labels are shown in warning message
✅ **Backward compatible** - Old address-based CSVs still work
✅ **Smart detection** - System auto-detects which format you're using

### Supported Colors

OFF, RED, GREEN, BLUE, YELLOW, CYAN, MAGENTA, WHITE

### Troubleshooting

**Items not importing?**
- Check Device labels in Devices tab match your CSV exactly
- Verify CSV uses commas as separators
- No headers needed - start with first data row

**See "skipped (unmapped)" warning?**
- Fix the label spelling in your CSV
- Download sample CSV to see registered device labels
- Use address format as fallback if label not registered

### Full Documentation

See `/docs/CSV_IMPORT_GUIDE.md` for complete details on formats, best practices, and troubleshooting.
