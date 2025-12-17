USE [TrainControllerSystem]
GO

/****** Object:  View [dbo].[V_PlatformEntry]    Script Date: 07-Dec-23 11:51:09 PM ******/
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO



CREATE OR ALTER   View [dbo].[V_PlatformEntry]
AS
WITH PlatformEntryView
AS(
SELECT Platforms.DB_ID as Platform_DB_ID
		, lsns.DB_ID
		, lsns.Section_DB_ID
		, lsns.NextSection_DB_ID
		, lsns.Direction
		, CASE WHEN lsns.Direction = 1 THEN Platforms.EndOfPlatformDir1 ELSE Platforms.EndOfPlatformDir0 END AS [EndOfPlatform]
		, '#' + CAST(lsns.Section_DB_ID AS nvarchar(max)) + '#' AS Route
		, 1 AS Level
FROM dbo.Lookup_Section_NextSection AS [lsns]
	JOIN
	dbo.Lookup_Sections_SubSections as lsss
	ON lsns.Section_DB_ID = lsss.Section_DB_ID
		AND lsns.Direction = lsss.Direction
	 JOIN
	 dbo.Platforms AS [Platforms]
	 ON lsss.SubSection_DB_ID = [Platforms].SubSection_DB_ID
WHERE lsns.IsActive = 1 AND
	  [Platforms].IsActive = 1
UNION ALL
SELECT 
	PEV.Platform_DB_ID
	, lsns.DB_ID
	, lsns.Section_DB_ID
	, lsns.NextSection_DB_ID
	, lsns.Direction
	, PEV.[EndOfPlatform]
	, CONCAT_WS(',', PEV.Route, '#' + CAST(lsns.Section_DB_ID AS nvarchar(max)) + '#') AS Route
	, PEV.Level + 1 AS Level
FROM [TrainControllerSystem].[dbo].[Lookup_Section_NextSection] as lsns
INNER JOIN PlatformEntryView AS PEV
			ON lsns.Section_DB_ID = PEV.NextSection_DB_ID
				AND
				lsns.Direction = PEV.Direction
WHERE CHARINDEX('#' + CAST(	PEV.[EndOfPlatform] AS nvarchar(max)) + '#', PEV.Route) = 0
)
SELECT DISTINCT
	PEV.Platform_DB_ID
	,CONCAT_WS(',', REPLACE(PEV.Route, '#', ''), PEV.NextSection_DB_ID) AS SectionsToLock
	, PEV.Direction
FROM PlatformEntryView as PEV
WHERE PEV.NextSection_DB_ID = PEV.EndOfPlatform
GO


