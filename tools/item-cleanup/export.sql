SET NOCOUNT ON;
WITH s AS (
  SELECT l.Item_ID, MAX(h.Doc_Date) LastSale, SUM(CASE WHEN h.Doc_Date >= DATEADD(day,-365,GETDATE()) THEN 1 ELSE 0 END) Sales12m
  FROM dbo.IQDoc_Delv l JOIN dbo.IQDoc_Header h ON h.Doc_Id=l.Doc_ID GROUP BY l.Item_ID),
p AS (
  SELECT l.Item_ID, MAX(h.Doc_Date) LastPurchase FROM dbo.IQDoc_Recd l JOIN dbo.IQDoc_Header h ON h.Doc_Id=l.Doc_ID WHERE h.Tt_Id IN (500,501,211) GROUP BY l.Item_ID)
SELECT m.Item_ID, LTRIM(RTRIM(ISNULL(m.Item_Code,''))), LTRIM(RTRIM(ISNULL(g.Grp_Name,''))), REPLACE(REPLACE(m.Item_Name, CHAR(9), ' '), CHAR(13), ' '),
  LTRIM(RTRIM(ISNULL(c.Item_CatName,''))), CAST(ISNULL(m.VAT,0) AS decimal(9,2)), LTRIM(RTRIM(ISNULL(m.HSNCODE,''))),
  CAST(ISNULL(m.Stk,0) AS decimal(18,3)), CAST(ISNULL(m.Item_Stat,0) AS int),
  CONVERT(char(10), s.LastSale, 120), ISNULL(s.Sales12m,0), CONVERT(char(10), p.LastPurchase, 120),
  CAST(ISNULL(u.Unit_WSrate,0) AS decimal(18,2)), CAST(ISNULL(u.Unit_Prate,0) AS decimal(18,2)), CAST(m.Grp_ID AS int)
FROM dbo.IQItems_Mast m
LEFT JOIN dbo.IQGroups g ON g.Grp_ID=m.Grp_ID
LEFT JOIN dbo.IQItems_Cat c ON c.Item_CatID=m.Cat_ID
LEFT JOIN dbo.IQItems_Units u ON u.Item_ID=m.Item_ID AND ISNULL(u.Recd_ID,0)=0
LEFT JOIN s ON s.Item_ID=m.Item_ID LEFT JOIN p ON p.Item_ID=m.Item_ID
ORDER BY g.Grp_Name, m.Item_Name;
