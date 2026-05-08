USE [TrainControllerSystem]
GO

/****** Object:  View [dbo].[V_Lookup_Section_NextSection]    Script Date: 07-Dec-23 11:50:28 PM ******/
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO



CREATE   VIEW [dbo].[V_Lookup_Section_NextSection]
AS 
SELECT  innerQuery.DB_ID
      ,innerQuery.[Section_DB_ID]
      ,innerQuery.[NextSection_DB_ID]
      ,innerQuery.[Direction]
	  ,innerQuery.DestinationsAgg as Destinations
	  , STRING_AGG(CONCAT_WS('=', s.Name, lsnss.SwitchState), ',') as [SwitchConstraints]
	  FROM
(SELECT  lsns.DB_ID
      ,lsns.[Section_DB_ID]
      ,[NextSection_DB_ID]
      ,[Direction]
      ,lsns.[IsActive]
	 , STRING_AGG(pl.DB_ID, ',') as DestinationsAgg
  FROM [TrainControllerSystem].[dbo].[Lookup_Section_NextSection] AS [lsns]
  JOIN dbo.Lookup_SectionNextSection_Destinations as lsnsd
  on lsns.DB_ID = lsnsd.Lookup_DB_ID
  join dbo.Platforms as pl
  on pl.DB_ID = lsnsd.Platform_DB_ID
   where lsns.IsActive = 1 
     GROUP BY lsns.DB_ID
			,lsns.Section_DB_ID
			,lsns.NextSection_DB_ID
			,lsns.Direction
			,lsns.[IsActive]) as innerQuery
  join dbo.Lookup_SectionNextSection_Switches as lsnss
  on lsnss.Lookup_DB_ID = innerQuery.DB_ID
  join dbo.Switches as s
  on lsnss.Switch_DB_ID = s.DB_ID
       GROUP BY innerQuery.DB_ID
			,innerQuery.Section_DB_ID
			,innerQuery.NextSection_DB_ID
			,innerQuery.Direction
			,innerQuery.[IsActive]
			,innerQuery.DestinationsAgg



GO


