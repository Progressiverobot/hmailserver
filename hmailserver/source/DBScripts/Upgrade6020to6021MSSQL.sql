ALTER TABLE hm_domains ADD domainrelayhost nvarchar(255) not null CONSTRAINT df_domainrelayhost DEFAULT ''

ALTER TABLE hm_domains ADD domainrelayport int not null CONSTRAINT df_domainrelayport DEFAULT 0

ALTER TABLE hm_domains ADD domainrelayrequiresauth int not null CONSTRAINT df_domainrelayrequiresauth DEFAULT 0

ALTER TABLE hm_domains ADD domainrelayusername nvarchar(255) not null CONSTRAINT df_domainrelayusername DEFAULT ''

ALTER TABLE hm_domains ADD domainrelaypassword nvarchar(255) not null CONSTRAINT df_domainrelaypassword DEFAULT ''

ALTER TABLE hm_domains ADD domainrelayconnectionsecurity int not null CONSTRAINT df_domainrelayconnectionsecurity DEFAULT 0

update hm_dbversion set value = 6021
