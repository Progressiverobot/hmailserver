ALTER TABLE hm_rule_criterias ALTER COLUMN criteriamatchvalue nvarchar(2000) not null

ALTER TABLE hm_accounts ADD accountvacationbegindate datetime not null DEFAULT '2001-01-01'

update hm_dbversion set value = 6012
