ALTER TABLE hm_domains ADD domainrelayhost nvarchar(255) not null DEFAULT ''

ALTER TABLE hm_domains ADD domainrelayport int not null DEFAULT 0

ALTER TABLE hm_domains ADD domainrelayrequiresauth int not null DEFAULT 0

ALTER TABLE hm_domains ADD domainrelayusername nvarchar(255) not null DEFAULT ''

ALTER TABLE hm_domains ADD domainrelaypassword nvarchar(255) not null DEFAULT ''

ALTER TABLE hm_domains ADD domainrelayconnectionsecurity int not null DEFAULT 0

update hm_dbversion set value = 6021
