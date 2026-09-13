ALTER TABLE hm_domains ALTER COLUMN domainrelaypassword nvarchar(1024) NOT NULL

update hm_dbversion set value = 6039
